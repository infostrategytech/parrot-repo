using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

public sealed class TikTokWebhookService(
    IOptions<TikTokSettings> options,
    ITikTokConnectionRepository connections,
    ITikTokCredentialProtector protector,
    InboundMessagePublisher publisher,
    IAppLogger<TikTokWebhookService> logger,
    TimeProvider time) : ITikTokWebhookService
{
    public async Task<bool> IsAuthenticAsync(string identifier, string rawBody, string? signature,
        CancellationToken cancellationToken = default)
    {
        TikTokConnection? connection = string.IsNullOrWhiteSpace(identifier)
            ? null
            : await connections.GetActiveByIdentifierAsync(identifier, cancellationToken);
        if (connection is null)
        {
            logger.LogWarning("TikTok webhook rejected because its connection is unknown or disabled.");
            return false;
        }

        string? reason;
        try
        {
            reason = TikTokWebhookSignature.Check(rawBody, signature, protector.Unprotect(connection.AppSecretEncrypted),
                time.GetUtcNow(), TimeSpan.FromSeconds(options.Value.SignatureToleranceSeconds));
        }
        catch (CryptographicException)
        {
            reason = "secret_unreadable";
        }

        if (reason is null) return true;

        logger.LogWarning("TikTok webhook rejected because its signature check failed. Reason={Reason} ConnectionId={ConnectionId}",
            reason, connection.Id);
        return false;
    }

    public async Task<bool> ProcessEventAsync(string identifier, string rawBody, CancellationToken cancellationToken = default)
    {
        TikTokConnection? connection = string.IsNullOrWhiteSpace(identifier)
            ? null
            : await connections.GetActiveByIdentifierAsync(identifier, cancellationToken);
        if (connection is null)
        {
            logger.LogWarning("TikTok webhook rejected because its connection is unknown or disabled.");
            return false;
        }

        if (connection.TikTokOpenId is null)
        {
            logger.LogWarning("TikTok webhook rejected because no account is authorized yet. ConnectionId={ConnectionId}", connection.Id);
            return false;
        }

        // Authentic but not an event we can read: a resend would be no different, so it is answered, not refused.
        TikTokWebhookEvent? tikTokEvent = TikTokWebhookEvents.Parse(rawBody);
        if (tikTokEvent is null)
        {
            logger.LogWarning("TikTok webhook ignored because its body is not an event. ConnectionId={ConnectionId}", connection.Id);
            return true;
        }

        // The event names the account it is about; a mismatch means this URL belongs to another customer.
        if (!string.Equals(tikTokEvent.BusinessOpenId, connection.TikTokOpenId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "TikTok webhook rejected because its account does not match. ConnectionId={ConnectionId} Event={Event} MessageId={MessageId}",
                connection.Id, tikTokEvent.Name, tikTokEvent.Message?.MessageId);
            return false;
        }

        // Only a customer's message is stored. im_send_msg is the business's own message coming back, and
        // read receipts and anything new TikTok adds are only noted, without anything the customer typed.
        if (tikTokEvent.Name != "im_receive_msg" || tikTokEvent.Message is not { } message)
        {
            logger.LogInformation("TikTok webhook event not stored. Event={Event} ConnectionId={ConnectionId}",
                tikTokEvent.Name, connection.Id);
            return true;
        }

        InboundMessage inbound = new()
        {
            MessageId = $"tiktok:{message.MessageId}",
            Source = MessagingPlatform.TikTok,
            Sender = ConversationKey.NormalizeParticipant(MessagingPlatform.TikTok, message.SenderId),
            Receiver = ConversationKey.NormalizeParticipant(MessagingPlatform.TikTok, connection.TikTokOpenId),
            ConversationKey = ConversationKey.Create(MessagingPlatform.TikTok, message.SenderId, connection.TikTokOpenId),
            Data = rawBody,
            SentAt = message.SentAt,
            ReceivedAt = time.GetUtcNow().UtcDateTime,
            CorrelationId = InboundMessagePublisher.CurrentCorrelationId(),
            ConnectionId = connection.Id,
            OwnerUserId = connection.WorkspaceId,
        };

        logger.LogInformation("TikTok webhook accepted. ConnectionId={ConnectionId}", connection.Id);
        await publisher.PublishAsync([inbound], cancellationToken);

        // Counted only once safely queued, so a webhook that failed and is resent counts once.
        MessageMetrics.Received.Add(1, MessageMetrics.MessageType(message.Type));
        return true;
    }
}
