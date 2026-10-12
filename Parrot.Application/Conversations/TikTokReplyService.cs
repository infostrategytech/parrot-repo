using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Conversations;

// Replies to a TikTok customer. ConversationService hands a reply here when its connection is the owner's TikTok one.
public sealed class TikTokReplyService(
    IConversationStore store,
    ITikTokConnectionRepository connections,
    TikTokAccessTokens tokens,
    ITikTokApiClient tikTok,
    IConversationSequence sequence,
    TimeProvider time)
{
    public async Task<TikTokConnection?> FindConnectionAsync(string owner, Guid connectionId, CancellationToken ct)
    {
        TikTokConnection? connection = await connections.GetByOwnerAsync(owner, ct);
        return connection?.Id == connectionId ? connection : null;
    }

    public async Task<ConversationMessage> SendAsync(TikTokConnection connection, string recipient,
        SendReplyRequest request, CancellationToken ct)
    {
        // The recipient is TikTok's open id for the customer: an opaque token, not a number.
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 4096
            || recipient.Length is < 1 or > 128 || recipient.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ValidationException("Supply a request ID, a recipient and text of 1–4096 characters.");
        if (connection.TikTokOpenId is null)
            throw new ConflictException("Authorize TikTok in the channel's setup before sending.");

        string owner = connection.OwnerUserId;
        MessageDocument? existing = await store.FindRequestAsync(owner, request.RequestId, ct);
        if (existing is not null) return ConversationService.Match(existing, connection.Id, recipient, request.Text);

        // The reply goes into the conversation TikTok named in the customer's last message.
        MessageDocument? latest = await store.LatestIncomingAsync(owner, connection.Id, recipient, ct);
        DateTime now = time.GetUtcNow().UtcDateTime;
        if (latest is not TikTokMessageDocument { ConversationId: { Length: > 0 } conversationId }
            || !ReplyWindow.IsOpen(MessagingPlatform.TikTok, latest.SentAt ?? latest.ReceivedAt, now))
            throw new ConflictException("Ask this customer to message your business first. TikTok replies require an incoming message within 48 hours.");

        // Before reserving, so a send that cannot be authorized leaves nothing pending.
        string accessToken = await tokens.GetAsync(connection, ct);

        string? conversationKey = latest.ConversationKey;
        long? replySequence = conversationKey is null ? null : await sequence.NextAsync(conversationKey, ct);

        TikTokMessageDocument doc = new()
        {
            MessageId = ConversationService.RequestKey(owner, request.RequestId), OwnerUserId = owner, ConnectionId = connection.Id,
            Platform = MessagingPlatform.TikTok, ExternalAccountId = connection.TikTokOpenId,
            ChannelId = connection.TikTokOpenId, ChannelDisplayName = connection.DisplayName ?? "",
            ConversationId = conversationId,
            // Outgoing replies never pass through a webhook, so SentAt is our own clock.
            From = recipient, Timestamp = new DateTimeOffset(now).ToUnixTimeMilliseconds().ToString(), SentAt = now,
            Type = "text", TextBody = request.Text, ReceivedAt = now, StoredAt = now,
            Outgoing = true, SendStatus = "pending",
            ConversationKey = conversationKey, Sequence = replySequence, Receiver = latest.Receiver,
        };
        if (!await store.ReserveAsync(doc, ct))
        {
            existing = await store.FindRequestAsync(owner, request.RequestId, ct)
                ?? throw new ConflictException("The send request is being processed. Check again shortly.");
            return ConversationService.Match(existing, connection.Id, recipient, request.Text);
        }

        // Once reserved, browser disconnection must not cancel an already dispatched send.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        string status;
        string? providerId = null;
        string errorCode = "none";
        try
        {
            providerId = await tikTok.SendTextAsync(connection.TikTokOpenId, accessToken, conversationId, request.Text, timeout.Token);
            status = "accepted";

            // From the customer's message arriving to TikTok accepting the reply.
            MessageMetrics.EndToEnd.Record(Math.Max(0, (now - latest.ReceivedAt).TotalSeconds));
        }
        catch (TikTokApiException ex)
        {
            // TikTok refused it, so it was not sent and the owner can try again.
            status = "failed";
            errorCode = ex.Code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            // Whether it went is not known, so it is never resent automatically.
            status = "unknown";
        }

        MessageMetrics.Sent.Add(
            1,
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("meta_error_code", errorCode));
        using CancellationTokenSource persistTimeout = new(TimeSpan.FromSeconds(10));
        await store.CompleteAsync(doc.MessageId, status, providerId, persistTimeout.Token);
        doc.SendStatus = status;
        doc.ProviderMessageId = providerId;
        return ConversationService.ToMessage(doc);
    }
}
