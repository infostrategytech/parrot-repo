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
    private readonly IWhatsAppTokenProtector _protector;
    private readonly IReplySendTimes? _sendTimes;
    private readonly IDeliveryReceipts? _receipts;
    private readonly IReceiptNotifier? _notifier;

    public WhatsAppWebhookService(
        IOptions<WhatsAppSettings> settings,
        IAppLogger<WhatsAppWebhookService> logger,
        InboundMessagePublisher publisher,
        IWhatsAppConnectionRepository connections,
        IWhatsAppTokenProtector protector,
        IReplySendTimes? sendTimes = null,
        IDeliveryReceipts? receipts = null,
        IReceiptNotifier? notifier = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _publisher = publisher;
        _connections = connections;
        _protector = protector;
        _sendTimes = sendTimes;
        _receipts = receipts;
        _notifier = notifier;
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

    public async Task<bool> IsAuthenticAsync(string? identifier, string rawBody, string? signature,
        CancellationToken cancellationToken = default)
    {
        if (_settings.SignatureMode == WebhookSignatureMode.Off)
        {
            return true;
        }

        // A manual connection's webhooks come from the customer's own Meta app and are
        // signed with its secret; everything else comes through Parrot's app.
        WhatsAppConnection? connection = string.IsNullOrWhiteSpace(identifier)
            ? null
            : await _connections.GetActiveByIdentifierAsync(identifier);
        string source = connection?.AppSecretEncrypted is null ? "app" : "connection";
        string? secret = source == "app" ? NullIfEmpty(_settings.AppSecret) : null;
        string? reason = null;

        if (source == "connection")
        {
            try
            {
                secret = _protector.Unprotect(connection!.AppSecretEncrypted!);
            }
            catch (CryptographicException)
            {
                reason = "secret_unreadable";
            }
        }

        reason ??= secret is null ? "no_app_secret"
            : signature is null ? "missing_signature"
            : WebhookSignature.Matches(rawBody, signature, secret) ? null
            : "mismatch";
        if (reason is null)
        {
            return true;
        }

        if (_settings.SignatureMode == WebhookSignatureMode.LogOnly)
        {
            _logger.LogWarning(
                "WhatsApp webhook signature check failed; accepted because SignatureMode is LogOnly. Reason={Reason} SecretSource={SecretSource} ConnectionId={ConnectionId}",
                reason, source, connection?.Id);
            return true;
        }

        _logger.LogWarning(
            "WhatsApp webhook rejected because its signature check failed. Reason={Reason} SecretSource={SecretSource} ConnectionId={ConnectionId}",
            reason, source, connection?.Id);
        return false;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
            // Customer messages in a rejected webhook are lost (answered 200, never queued): the ids say which.
            List<WhatsAppMessage> dropped = payload.Entry
                .SelectMany(entry => entry.Changes)
                .Where(change => change.Field == "messages")
                .SelectMany(change => change.Value.Messages ?? [])
                .ToList();
            int droppedStatuses = payload.Entry
                .SelectMany(entry => entry.Changes)
                .Where(change => change.Field == "messages")
                .Sum(change => change.Value.Statuses?.Count ?? 0);
            _logger.LogWarning(
                "WhatsApp webhook rejected: account ID mismatch. ConnectionId={ConnectionId} Expected={Expected} Received={Received} ExpectedPhoneNumberId={ExpectedPhoneNumberId} PhoneNumberId={PhoneNumberId} DroppedMessages={DroppedMessages} DroppedMessageIds={DroppedMessageIds} DroppedStatuses={DroppedStatuses}",
                connection.Id,
                connection.WhatsAppBusinessAccountId,
                mismatch?.Id,
                connection.PhoneNumberId,
                mismatch?.Changes.FirstOrDefault(change => change.Field == "messages")?.Value.Metadata.PhoneNumberId,
                dropped.Count,
                string.Join(',', dropped.Select(message => $"whatsapp:{message.Id}")),
                droppedStatuses);
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
            await RecordStatusAsync(status, connectionId, ownerUserId, budget.Token);
        }
    }

    // Counts the status, records it on the reply Parrot sent, and measures how long Meta took to
    // report it. Never fails or delays the webhook: once the lookup budget is spent the status is
    // still counted, only the rest is skipped.
    private async Task RecordStatusAsync(WhatsAppMessageStatus status, Guid? connectionId, string? ownerUserId, CancellationToken budget)
    {
        string label = status.Status is "sent" or "delivered" or "read" or "failed" ? status.Status : "other";
        KeyValuePair<string, object?> tag = new("status", label);
        MessageMetrics.StatusUpdates.Add(1, tag);

        if (budget.IsCancellationRequested)
        {
            return;
        }

        // Only a connection's own webhook can say which replies are its. "sent" is Meta accepting the
        // message, which the send already recorded.
        if (_receipts is not null && connectionId is { } connection && ReceiptState(status.Status) is { } state)
        {
            try
            {
                await _receipts.RecordAsync(connection, status.Id, state,
                    ProviderTimestamp.FromUnixSeconds(status.Timestamp) ?? DateTime.UtcNow, budget);

                // The open inbox re-reads the thread. The customer is who the receipt names, as the thread's sender.
                if (_notifier is not null && ownerUserId is not null)
                {
                    string? customer = string.IsNullOrWhiteSpace(status.RecipientId)
                        ? null
                        : ConversationKey.NormalizeParticipant(MessagingPlatform.WhatsApp, status.RecipientId);
                    await _notifier.ReceiptsChangedAsync(ownerUserId, connection, customer, budget);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("WhatsApp receipt not recorded. Status={Status} Reason={Reason}", label, ex.Message);
            }
        }

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

    private static DeliveryState? ReceiptState(string status) => status switch
    {
        "delivered" => DeliveryState.Delivered,
        "read" => DeliveryState.Read,
        "failed" => DeliveryState.Failed,
        _ => null,
    };
}
