using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

// Instagram DMs arrive in Messenger's shape (object "instagram", entry[].messaging[]), with entry.id the
// business's professional account id and sender.id the customer's Instagram-scoped id (IGSID).
public sealed class InstagramWebhookService(
    IOptions<InstagramSettings> settings,
    IInstagramConnectionRepository connections,
    InboundMessagePublisher publisher,
    IAppLogger<InstagramWebhookService> logger,
    IInstagramCredentialProtector protector) : IInstagramWebhookService
{
    private readonly InstagramSettings _settings = settings.Value;

    public async Task<bool> IsAuthenticAsync(string? identifier, string rawBody, string? signature,
        CancellationToken cancellationToken = default)
    {
        if (_settings.SignatureMode == WebhookSignatureMode.Off)
        {
            return true;
        }

        // A manual connection's webhooks are signed with its Instagram app secret; anything else
        // would come through Parrot's own app.
        InstagramConnection? connection = string.IsNullOrWhiteSpace(identifier)
            ? null
            : await connections.GetActiveByIdentifierAsync(identifier, cancellationToken);
        string source = connection?.AppSecretEncrypted is null ? "app" : "connection";
        string? secret = source == "app" && !string.IsNullOrWhiteSpace(_settings.AppSecret) ? _settings.AppSecret : null;
        string? reason = null;

        if (source == "connection")
        {
            try
            {
                secret = protector.Unprotect(connection!.AppSecretEncrypted!);
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
            logger.LogWarning(
                "Instagram webhook signature check failed; accepted because SignatureMode is LogOnly. Reason={Reason} SecretSource={SecretSource} ConnectionId={ConnectionId}",
                reason, source, connection?.Id);
            return true;
        }

        logger.LogWarning(
            "Instagram webhook rejected because its signature check failed. Reason={Reason} SecretSource={SecretSource} ConnectionId={ConnectionId}",
            reason, source, connection?.Id);
        return false;
    }

    public async Task<bool> ProcessConnectionEventAsync(string identifier, FacebookWebhookPayload payload, string rawBody,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            logger.LogWarning("Instagram webhook rejected because its connection reference is empty.");
            return false;
        }

        if (payload.Object != "instagram")
        {
            logger.LogWarning("Instagram webhook rejected because its object is not instagram. Object={Object}", payload.Object);
            return false;
        }

        InstagramConnection? connection = await connections.GetActiveByIdentifierAsync(identifier, cancellationToken);
        if (connection is null)
        {
            logger.LogWarning("Instagram webhook rejected because its connection reference is unknown or disabled.");
            return false;
        }

        if (connection.WebhookVerifiedAt is null)
        {
            logger.LogWarning("Instagram webhook rejected for unverified connection. ConnectionId={ConnectionId}", connection.Id);
            return false;
        }

        if (string.IsNullOrEmpty(connection.AccountId))
        {
            logger.LogWarning("Instagram webhook rejected because no account is saved. ConnectionId={ConnectionId}", connection.Id);
            return false;
        }

        // entry.id is the professional account. A mismatch means the ?ref= belongs to another account.
        if (payload.Entry.Count == 0 || payload.Entry.Any(entry => entry.Id != connection.AccountId))
        {
            logger.LogWarning(
                "Instagram webhook rejected because account IDs do not match. ConnectionId={ConnectionId} Expected={Expected} Received={Received} DroppedEvents={DroppedEvents}",
                connection.Id,
                connection.AccountId,
                string.Join(',', payload.Entry.Select(entry => entry.Id)),
                payload.Entry.Sum(entry => entry.Messaging?.Count ?? 0));
            return false;
        }

        logger.LogInformation("Instagram webhook accepted. ConnectionId={ConnectionId} Entries={EntryCount}",
            connection.Id, payload.Entry.Count);

        string correlationId = InboundMessagePublisher.CurrentCorrelationId();
        DateTime receivedAt = DateTime.UtcNow;
        List<InboundMessage> inbound = [];
        List<string> types = [];

        foreach (FacebookEntry entry in payload.Entry)
        {
            foreach (FacebookMessagingEvent evt in entry.Messaging ?? [])
            {
                if (ToInbound(connection, evt, rawBody, correlationId, receivedAt) is { } message)
                {
                    inbound.Add(message);
                    types.Add(evt.Message is null ? "postback"
                        : evt.Message.Attachments is { Count: > 0 } attachments ? attachments[0].Type : "text");
                }
            }
        }

        await publisher.PublishAsync(inbound, cancellationToken);

        // Counted only once safely queued, so a webhook that failed and is resent counts once.
        foreach (string type in types)
        {
            MessageMetrics.Received.Add(1, MessageMetrics.MessageType(type));
        }

        return true;
    }

    private InboundMessage? ToInbound(InstagramConnection connection, FacebookMessagingEvent evt, string rawBody,
        string correlationId, DateTime receivedAt)
    {
        // Echoes are the business's own messages, from Parrot or the Instagram app, coming back.
        if (evt.Message?.IsEcho == true)
        {
            return null;
        }

        string? mid = evt.Message?.Mid ?? evt.Postback?.Mid;
        if (mid is null)
        {
            if (evt.Read is not null || evt.Reaction is not null)
            {
                logger.LogInformation("Instagram {Event} notification. ConnectionId={ConnectionId}",
                    evt.Read is not null ? "read" : "reaction", connection.Id);
            }

            return null;
        }

        return new InboundMessage
        {
            MessageId = $"instagram:{mid}",
            Source = MessagingPlatform.Instagram,
            Sender = ConversationKey.NormalizeParticipant(MessagingPlatform.Instagram, evt.Sender.Id),
            Receiver = ConversationKey.NormalizeParticipant(MessagingPlatform.Instagram, connection.AccountId!),
            ConversationKey = ConversationKey.Create(MessagingPlatform.Instagram, evt.Sender.Id, connection.AccountId!),
            Data = rawBody,

            // Instagram counts in milliseconds, as Messenger does.
            SentAt = ProviderTimestamp.FromUnixMilliseconds(evt.Timestamp),
            ReceivedAt = receivedAt,
            CorrelationId = correlationId,
            ConnectionId = connection.Id,
            OwnerUserId = connection.OwnerUserId,
        };
    }
}
