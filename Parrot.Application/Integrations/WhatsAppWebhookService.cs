using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Parrot.Application.Conversations;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

public class WhatsAppWebhookService : IWhatsAppWebhookService
{
    // How long one webhook may spend looking up send times for delivery latency.
    private static readonly TimeSpan StatusLookupBudget = TimeSpan.FromSeconds(1);

    private readonly WhatsAppSettings _settings;
    private readonly IAppLogger<WhatsAppWebhookService> _logger;
    private readonly InboundMessagePublisher _publisher;
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly IReplySendTimes? _sendTimes;

    public WhatsAppWebhookService(
        IOptions<WhatsAppSettings> settings,
        IAppLogger<WhatsAppWebhookService> logger,
        InboundMessagePublisher publisher,
        IWhatsAppConnectionRepository connections,
        IReplySendTimes? sendTimes = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _publisher = publisher;
        _connections = connections;
        _sendTimes = sendTimes;
    }

    public string GetWebhookUrl() =>
        $"{_settings.WebhookBaseUrl.TrimEnd('/')}/api/webhooks/whatsapp";

    public bool TryVerifyChallenge(string hubMode, string hubVerifyToken, string hubChallenge, out string echoChallenge)
    {
        echoChallenge = string.Empty;

        if (hubMode != "subscribe" || hubVerifyToken != _settings.VerifyToken)
        {
            _logger.LogWarning("WhatsApp webhook challenge rejected. Mode={Mode} TokenMatch={Match}", hubMode, hubVerifyToken == _settings.VerifyToken);
            return false;
        }

        echoChallenge = hubChallenge;
        return true;
    }

    public bool IsValidSignature(string rawBody, string signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            _logger.LogError("WhatsApp webhook rejected because WhatsApp:AppSecret is not configured.");
            return false;
        }

        if (!signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("WhatsApp webhook rejected because X-Hub-Signature-256 is malformed.");
            return false;
        }

        string receivedHash = signatureHeader["sha256=".Length..];
        if (receivedHash.Length != 64)
        {
            _logger.LogWarning("WhatsApp webhook rejected because X-Hub-Signature-256 has an invalid length.");
            return false;
        }

        byte[] expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_settings.AppSecret),
            Encoding.UTF8.GetBytes(rawBody));

        try
        {
            byte[] received = Convert.FromHexString(receivedHash);
            bool valid = CryptographicOperations.FixedTimeEquals(received, expected);
            if (!valid)
            {
                _logger.LogWarning("WhatsApp webhook rejected because its signature does not match the configured App Secret.");
            }

            return valid;
            
        }
        catch (FormatException)
        {
            _logger.LogWarning("WhatsApp webhook rejected because X-Hub-Signature-256 is not hexadecimal.");
            return false;
        }
    }

    public Task ProcessEventAsync(WhatsAppWebhookPayload payload, string rawBody, CancellationToken cancellationToken = default) =>
        ProcessEventAsync(payload, rawBody, null, null, cancellationToken);

    public async Task<bool> ProcessConnectionEventAsync(
        string identifier,
        WhatsAppWebhookPayload payload,
        string rawBody,
        CancellationToken cancellationToken = default)
    {
        // A false return is answered 200 and the event dropped (quarantined in
        // the logs): the request is authentic, so a retry would fail the same way.
        if (string.IsNullOrWhiteSpace(identifier))
        {
            _logger.LogWarning("WhatsApp webhook rejected because its connection reference is empty.");
            return Rejected("unknown_connection");
        }

        WhatsAppConnection? connection = await _connections.GetActiveByIdentifierAsync(identifier);
        if (connection is null)
        {
            _logger.LogWarning("WhatsApp webhook rejected because its connection reference is unknown or disabled.");
            return Rejected("unknown_connection");
        }

        if (connection.WebhookVerifiedAt is null)
        {
            _logger.LogWarning("WhatsApp webhook rejected for unverified connection. ConnectionId={ConnectionId}", connection.Id);
            return Rejected("unknown_connection");
        }

        if (string.IsNullOrEmpty(connection.WhatsAppBusinessAccountId)
            || string.IsNullOrEmpty(connection.PhoneNumberId))
        {
            _logger.LogWarning("WhatsApp webhook rejected because account IDs are incomplete. ConnectionId={ConnectionId}", connection.Id);
            return Rejected("unknown_connection");
        }

        WhatsAppEntry? mismatch = payload.Entry.FirstOrDefault(entry => entry.Id != connection.WhatsAppBusinessAccountId
            || entry.Changes.Any(change => change.Field == "messages"
                && change.Value.Metadata.PhoneNumberId != connection.PhoneNumberId));
        if (payload.Entry.Count == 0 || mismatch is not null)
        {
            // entry[].id is the WABA ID: check the stored value is that, not the
            // Business Manager or phone number ID. Meta's dashboard test events
            // carry a placeholder ID and always mismatch.
            _logger.LogWarning(
                "WhatsApp webhook rejected: account ID mismatch. ConnectionId={ConnectionId} Expected={Expected} Received={Received} ExpectedPhoneNumberId={ExpectedPhoneNumberId} PhoneNumberId={PhoneNumberId}",
                connection.Id,
                connection.WhatsAppBusinessAccountId,
                mismatch?.Id,
                connection.PhoneNumberId,
                mismatch?.Changes.FirstOrDefault(change => change.Field == "messages")?.Value.Metadata.PhoneNumberId);
            return Rejected("account_mismatch");
        }

        _logger.LogInformation(
            "WhatsApp webhook accepted. ConnectionId={ConnectionId} Entries={EntryCount}",
            connection.Id,
            payload.Entry.Count);

        await ProcessEventAsync(payload, rawBody, connection.Id, connection.OwnerUserId, cancellationToken);
        return true;
    }

    private static bool Rejected(string result)
    {
        MessageMetrics.Webhooks.Add(1, new KeyValuePair<string, object?>("result", result));
        return false;
    }

    // Reads only what routing needs from each message and hands the body on as
    // received; the Message Worker decodes the rest.
    private async Task ProcessEventAsync(
        WhatsAppWebhookPayload payload,
        string rawBody,
        Guid? connectionId,
        string? ownerUserId,
        CancellationToken cancellationToken)
    {
        string correlationId = InboundMessagePublisher.CurrentCorrelationId();
        DateTime receivedAt = DateTime.UtcNow;
        List<InboundMessage> inbound = [];
        List<string> types = [];
        List<WhatsAppMessageStatus> statuses = [];

        foreach (WhatsAppEntry entry in payload.Entry)
        {
            foreach (WhatsAppChange change in entry.Changes)
            {
                if (change.Field != "messages")
                {
                    continue;
                }

                string business = change.Value.Metadata.DisplayPhoneNumber;

                foreach (WhatsAppMessage msg in change.Value.Messages ?? [])
                {
                    types.Add(msg.Type);
                    inbound.Add(new InboundMessage
                    {
                        MessageId = $"whatsapp:{msg.Id}",
                        Source = MessagingPlatform.WhatsApp,
                        Sender = ConversationKey.NormalizeParticipant(MessagingPlatform.WhatsApp, msg.From),
                        Receiver = ConversationKey.NormalizeParticipant(MessagingPlatform.WhatsApp, business),
                        ConversationKey = ConversationKey.Create(MessagingPlatform.WhatsApp, msg.From, business),
                        Data = rawBody,

                        // WhatsApp sends whole seconds; Messenger sends milliseconds.
                        SentAt = ProviderTimestamp.FromUnixSeconds(msg.Timestamp),
                        ReceivedAt = receivedAt,
                        CorrelationId = correlationId,
                        ConnectionId = connectionId,
                        OwnerUserId = ownerUserId,
                    });
                }

                foreach (WhatsAppMessageStatus status in change.Value.Statuses ?? [])
                {
                    statuses.Add(status);
                    if (status.Status != "failed")
                    {
                        _logger.LogInformation(
                            "WhatsApp status update. MessageId={Id} Status={Status}",
                            status.Id,
                            status.Status);
                        continue;
                    }

                    // A reply the customer never got. Meta says why; without it a
                    // failure can't be told apart from the next one.
                    List<WhatsAppStatusError?> reasons = status.Errors is { Count: > 0 } ? [.. status.Errors] : [null];
                    foreach (WhatsAppStatusError? error in reasons)
                    {
                        _logger.LogWarning(
                            "WhatsApp message failed. MessageId={Id} ErrorCode={ErrorCode} ErrorTitle={ErrorTitle} ErrorDetails={ErrorDetails}",
                            status.Id,
                            error?.Code,
                            error?.Title ?? error?.Message,
                            error?.ErrorData?.Details);
                    }
                }
            }
        }

        await _publisher.PublishAsync(inbound, cancellationToken);

        // Only now: a webhook that failed to publish answers 503 and comes back,
        // and must not be counted twice.
        MessageMetrics.Webhooks.Add(1, new KeyValuePair<string, object?>("result", "ok"));
        foreach (string type in types)
        {
            MessageMetrics.Received.Add(1, MessageMetrics.MessageType(type));
        }

        if (statuses.Count == 0)
        {
            return;
        }

        // Latency lookups get one short budget for the whole webhook: a slow or
        // unreachable database must never hold up the 200 Meta is waiting for.
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(StatusLookupBudget);
        foreach (WhatsAppMessageStatus status in statuses)
        {
            await RecordStatusAsync(status, budget.Token);
        }
    }

    // Counts the status and, for a reply Parrot sent, how long Meta took to report
    // it. Never fails or delays the webhook: once the lookup budget is spent the
    // status is still counted, only its latency sample is skipped.
    private async Task RecordStatusAsync(WhatsAppMessageStatus status, CancellationToken budget)
    {
        string label = status.Status is "sent" or "delivered" or "read" or "failed" ? status.Status : "other";
        KeyValuePair<string, object?> tag = new("status", label);
        MessageMetrics.StatusUpdates.Add(1, tag);

        if (_sendTimes is null || budget.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (await _sendTimes.SentAtAsync(status.Id, budget) is { } sentAt)
            {
                MessageMetrics.DeliveryLatency.Record(Math.Max(0, (DateTime.UtcNow - sentAt).TotalSeconds), tag);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("WhatsApp status latency not recorded. Status={Status} Reason={Reason}", label, ex.Message);
        }
    }
}
