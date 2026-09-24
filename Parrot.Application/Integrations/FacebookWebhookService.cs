using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

// The connection a webhook event was routed to, flattened for message building.
public sealed record FacebookPageRegistration(Guid ConnectionId, string OwnerUserId, string PageId, string PageName);

public sealed class FacebookWebhookService : IFacebookWebhookService
{
    private readonly FacebookSettings _settings;
    private readonly IFacebookConnectionRepository _connections;
    private readonly IMessagePublisher _publisher;
    private readonly IAppLogger<FacebookWebhookService> _logger;

    public FacebookWebhookService(
        IOptions<FacebookSettings> settings,
        IFacebookConnectionRepository connections,
        IMessagePublisher publisher,
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

        FacebookPageRegistration registration = new(
            connection.Id, connection.OwnerUserId, connection.PageId!, connection.PageName ?? connection.PageId!);

        _logger.LogInformation(
            "Facebook webhook accepted. ConnectionId={ConnectionId} Entries={EntryCount}",
            connection.Id,
            payload.Entry.Count);

        foreach (FacebookEntry entry in payload.Entry)
        {
            if (entry.Messaging is { Count: > 0 } messagingEvents)
                await ProcessMessagingEventsAsync(registration, messagingEvents, cancellationToken);

            if (entry.Changes is { Count: > 0 } changes)
                ProcessPageChanges(entry.Id, registration.OwnerUserId, changes);
        }

        return true;
    }

    private async Task ProcessMessagingEventsAsync(
        FacebookPageRegistration registration,
        List<FacebookMessagingEvent> events,
        CancellationToken cancellationToken)
    {
        List<IncomingMessage> batch = new(events.Count);

        foreach (FacebookMessagingEvent evt in events)
        {
            // Ignore echo messages sent by the page itself
            if (evt.Message?.IsEcho == true)
                continue;

            if (evt.Message is not null)
            {
                _logger.LogInformation(
                    "Facebook message received. TenantId={TenantId} PageId={PageId} SenderId={Sender} Mid={Mid}",
                    registration.OwnerUserId, registration.PageId, evt.Sender.Id, evt.Message.Mid);

                string type = evt.Message.Attachments is { Count: > 0 }
                    ? evt.Message.Attachments[0].Type
                    : "text";

                // Without these the payload is lost and an "image" message can never be retrieved.
                List<string>? attachmentUrls = evt.Message.Attachments?
                    .Select(a => a.Payload?.Url)
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Select(url => url!)
                    .ToList();

                batch.Add(ToIncoming(registration, evt, evt.Message.Mid, type, evt.Message.Text,
                    attachmentUrls is { Count: > 0 } ? attachmentUrls : null));
                continue;
            }

            if (evt.Postback is not null)
            {
                _logger.LogInformation(
                    "Facebook postback received. TenantId={TenantId} PageId={PageId} SenderId={Sender} Payload={Payload}",
                    registration.OwnerUserId, registration.PageId, evt.Sender.Id, evt.Postback.Payload);

                batch.Add(ToIncoming(registration, evt, evt.Postback.Mid, "postback", evt.Postback.Payload));
                continue;
            }

            if (evt.Delivery is not null)
            {
                _logger.LogInformation(
                    "Facebook delivery confirmation. TenantId={TenantId} PageId={PageId} Watermark={Watermark}",
                    registration.OwnerUserId, registration.PageId, evt.Delivery.Watermark);
                continue;
            }

            if (evt.Read is not null)
            {
                _logger.LogInformation(
                    "Facebook read confirmation. TenantId={TenantId} PageId={PageId} Watermark={Watermark}",
                    registration.OwnerUserId, registration.PageId, evt.Read.Watermark);
                continue;
            }

            if (evt.Reaction is not null)
            {
                _logger.LogInformation(
                    "Facebook reaction. TenantId={TenantId} PageId={PageId} Mid={Mid} Action={Action}",
                    registration.OwnerUserId, registration.PageId, evt.Reaction.Mid, evt.Reaction.Action);
            }
        }

        if (batch.Count > 0)
            await _publisher.PublishBatchAsync(batch, cancellationToken);
    }

    private static IncomingMessage ToIncoming(
        FacebookPageRegistration registration,
        FacebookMessagingEvent evt,
        string messageId,
        string type,
        string? textBody,
        IReadOnlyList<string>? attachmentUrls = null) => new()
    {
        ConnectionId = registration.ConnectionId,
        OwnerUserId = registration.OwnerUserId,
        MessageId = messageId,
        Platform = MessagingPlatform.Facebook,
        ExternalAccountId = registration.PageId,
        From = evt.Sender.Id,
        Timestamp = evt.Timestamp.ToString(),
        SentAt = ProviderTimestamp.FromUnixMilliseconds(evt.Timestamp),
        Type = type,
        TextBody = textBody,
        AttachmentUrls = attachmentUrls,
        ChannelId = registration.PageId,
        ChannelDisplayName = registration.PageName,
        ReceivedAt = DateTime.UtcNow,
    };

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
