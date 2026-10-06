using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

// The connection a webhook event was routed to, flattened for message building.
public sealed record FacebookPageRegistration(Guid ConnectionId, string OwnerUserId, string PageId);

public sealed class FacebookWebhookService : IFacebookWebhookService
{
    private readonly FacebookSettings _settings;
    private readonly IFacebookConnectionRepository _connections;
    private readonly InboundMessagePublisher _publisher;
    private readonly IAppLogger<FacebookWebhookService> _logger;

    public FacebookWebhookService(
        IOptions<FacebookSettings> settings,
        IFacebookConnectionRepository connections,
        InboundMessagePublisher publisher,
        IAppLogger<FacebookWebhookService> logger)
    {
        _settings = settings.Value;
        _connections = connections;
        _publisher = publisher;
        _logger = logger;
    }

    public string GetWebhookUrl() =>
        $"{_settings.WebhookBaseUrl.TrimEnd('/')}/api/webhooks/facebook";

    public bool IsValidSignature(string rawBody, string signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            _logger.LogError("Facebook webhook rejected because Facebook:AppSecret is not configured.");
            return false;
        }

        if (!signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            return false;

        string receivedHash = signatureHeader["sha256=".Length..];
        if (receivedHash.Length != 64)
            return false;

        byte[] expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_settings.AppSecret),
            Encoding.UTF8.GetBytes(rawBody));

        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(receivedHash), expected);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Facebook webhook rejected because X-Hub-Signature-256 is not hexadecimal.");
            return false;
        }
    }

    public async Task<bool> ProcessConnectionEventAsync(
        string identifier,
        FacebookWebhookPayload payload,
        string rawBody,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            _logger.LogWarning("Facebook webhook rejected because its connection reference is empty.");
            return false;
        }

        if (payload.Object is not ("page" or "instagram"))
        {
            _logger.LogWarning("Unrecognised Facebook webhook object type: {Object}", payload.Object);
            return false;
        }

        FacebookConnection? connection = await _connections.GetActiveByIdentifierAsync(identifier, cancellationToken);
        if (connection is null)
        {
            _logger.LogWarning("Facebook webhook rejected because its connection reference is unknown or disabled.");
            return false;
        }

        if (connection.WebhookVerifiedAt is null)
        {
            _logger.LogWarning("Facebook webhook rejected for unverified connection. ConnectionId={ConnectionId}", connection.Id);
            return false;
        }

        if (string.IsNullOrEmpty(connection.PageId))
        {
            _logger.LogWarning("Facebook webhook rejected because no Page is linked. ConnectionId={ConnectionId}", connection.Id);
            return false;
        }

        // For Messenger, entry.id is the Page ID. A mismatch means the ?ref= belongs to a different customer.
        if (payload.Entry.Count == 0 || payload.Entry.Any(entry => entry.Id != connection.PageId))
        {
            _logger.LogWarning("Facebook webhook rejected because Page IDs do not match. ConnectionId={ConnectionId}", connection.Id);
            return false;
        }

        FacebookPageRegistration registration = new FacebookPageRegistration(connection.Id, connection.OwnerUserId, connection.PageId!);

        _logger.LogInformation(
            "Facebook webhook accepted. ConnectionId={ConnectionId} Entries={EntryCount}",
            connection.Id,
            payload.Entry.Count);

        string correlationId = InboundMessagePublisher.CurrentCorrelationId();
        DateTime receivedAt = DateTime.UtcNow;
        List<InboundMessage> inbound = [];

        foreach (FacebookEntry entry in payload.Entry)
        {
            if (entry.Messaging is { Count: > 0 } messagingEvents)
            {
                foreach (FacebookMessagingEvent evt in messagingEvents)
                {
                    if (ToInbound(registration, evt, rawBody, correlationId, receivedAt) is { } message)
                    {
                        inbound.Add(message);
                    }
                }
            }

            if (entry.Changes is { Count: > 0 } changes)
                ProcessPageChanges(entry.Id, registration.OwnerUserId, changes);
        }

        await _publisher.PublishAsync(inbound, cancellationToken);
        return true;
    }

    // A customer's message or postback becomes an InboundMessage; the body goes
    // on as received for the Message Worker to decode. Everything else is only
    // logged, without the customer's id or anything they typed.
    private InboundMessage? ToInbound(
        FacebookPageRegistration registration,
        FacebookMessagingEvent evt,
        string rawBody,
        string correlationId,
        DateTime receivedAt)
    {
        // Echoes are the Page's own replies coming back.
        if (evt.Message?.IsEcho == true)
        {
            return null;
        }

        string? mid = evt.Message?.Mid ?? evt.Postback?.Mid;
        if (mid is not null)
        {
            return new InboundMessage
            {
                MessageId = $"facebook:{mid}",
                Source = MessagingPlatform.Facebook,
                Sender = ConversationKey.NormalizeParticipant(MessagingPlatform.Facebook, evt.Sender.Id),
                Receiver = ConversationKey.NormalizeParticipant(MessagingPlatform.Facebook, registration.PageId),
                ConversationKey = ConversationKey.Create(MessagingPlatform.Facebook, evt.Sender.Id, registration.PageId),
                Data = rawBody,

                // Messenger sends milliseconds; WhatsApp sends whole seconds.
                SentAt = ProviderTimestamp.FromUnixMilliseconds(evt.Timestamp),
                ReceivedAt = receivedAt,
                CorrelationId = correlationId,
                ConnectionId = registration.ConnectionId,
                OwnerUserId = registration.OwnerUserId,
            };
        }

        if (evt.Delivery is not null)
        {
            _logger.LogInformation(
                "Facebook delivery confirmation. TenantId={TenantId} PageId={PageId} Watermark={Watermark}",
                registration.OwnerUserId,
                registration.PageId,
                evt.Delivery.Watermark);
        }
        else if (evt.Read is not null)
        {
            _logger.LogInformation(
                "Facebook read confirmation. TenantId={TenantId} PageId={PageId} Watermark={Watermark}",
                registration.OwnerUserId,
                registration.PageId,
                evt.Read.Watermark);
        }
        else if (evt.Reaction is not null)
        {
            _logger.LogInformation(
                "Facebook reaction. TenantId={TenantId} PageId={PageId} Mid={Mid} Action={Action}",
                registration.OwnerUserId,
                registration.PageId,
                evt.Reaction.Mid,
                evt.Reaction.Action);
        }

        return null;
    }

    private void ProcessPageChanges(string pageId, string tenantId, List<FacebookChange> changes)
    {
        foreach (FacebookChange change in changes)
        {
            _logger.LogInformation(
                "Facebook page change. TenantId={TenantId} PageId={PageId} Field={Field}",
                tenantId, pageId, change.Field);
        }
    }
}
