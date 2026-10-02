using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

public class WhatsAppWebhookService : IWhatsAppWebhookService
{
    private readonly WhatsAppSettings _settings;
    private readonly IAppLogger<WhatsAppWebhookService> _logger;
    private readonly InboundMessagePublisher _publisher;
    private readonly IWhatsAppConnectionRepository _connections;

    public WhatsAppWebhookService(
        IOptions<WhatsAppSettings> settings,
        IAppLogger<WhatsAppWebhookService> logger,
        InboundMessagePublisher publisher,
        IWhatsAppConnectionRepository connections)
    {
        _settings = settings.Value;
        _logger = logger;
        _publisher = publisher;
        _connections = connections;
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
        if (string.IsNullOrWhiteSpace(identifier))
        {
            _logger.LogWarning("WhatsApp webhook rejected because its connection reference is empty.");
            return false;
        }

        WhatsAppConnection? connection = await _connections.GetActiveByIdentifierAsync(identifier);
        if (connection is null)
        {
            _logger.LogWarning("WhatsApp webhook rejected because its connection reference is unknown or disabled.");
            return false;
        }

        if (connection.WebhookVerifiedAt is null)
        {
            _logger.LogWarning("WhatsApp webhook rejected for unverified connection. ConnectionId={ConnectionId}", connection.Id);
            return false;
        }

        if (string.IsNullOrEmpty(connection.WhatsAppBusinessAccountId)
            || string.IsNullOrEmpty(connection.PhoneNumberId))
        {
            _logger.LogWarning("WhatsApp webhook rejected because account IDs are incomplete. ConnectionId={ConnectionId}", connection.Id);
            return false;
        }

        if (payload.Entry.Count == 0
            || payload.Entry.Any(entry => entry.Id != connection.WhatsAppBusinessAccountId
                || entry.Changes.Any(change => change.Field == "messages"
                    && change.Value.Metadata.PhoneNumberId != connection.PhoneNumberId)))
        {
            _logger.LogWarning("WhatsApp webhook rejected because Meta account IDs do not match. ConnectionId={ConnectionId}", connection.Id);
            return false;
        }

        _logger.LogInformation(
            "WhatsApp webhook accepted. ConnectionId={ConnectionId} Entries={EntryCount}",
            connection.Id,
            payload.Entry.Count);

        await ProcessEventAsync(payload, rawBody, connection.Id, connection.OwnerUserId, cancellationToken);
        return true;
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
                    _logger.LogInformation(
                        "WhatsApp status update. MessageId={Id} Status={Status}",
                        status.Id,
                        status.Status);
                }
            }
        }

        await _publisher.PublishAsync(inbound, cancellationToken);
    }
}
