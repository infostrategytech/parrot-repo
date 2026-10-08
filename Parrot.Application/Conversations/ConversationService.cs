using System.Security.Cryptography;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Conversations;

public sealed class ConversationService(IConversationStore store, IWhatsAppConnectionRepository connections,
    IWhatsAppSender sender, IWhatsAppTokenProtector protector, IConversationSequence sequence)
{
    public async Task<ConversationMessage> SendAsync(string owner, Guid connectionId, string recipient,
        SendReplyRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 4096
            || recipient.Length is < 1 or > 20 || !recipient.All(char.IsAsciiDigit))
            throw new ValidationException("Supply a request ID, a numeric recipient and text of 1–4096 characters.");
        Parrot.Domain.Entities.WhatsAppConnection connection = await connections.GetByOwnerAndIdAsync(owner, connectionId)
            ?? throw new NotFoundException("Connection not found.");
        if (connection.DisabledAt.HasValue || connection.WebhookVerifiedAt is null)
            throw new ConflictException("This connection is disabled or unverified.");

        MessageDocument? existing = await store.FindRequestAsync(owner, request.RequestId, ct);
        if (existing is not null) return Match(existing, connectionId, recipient, request.Text);

        if (string.IsNullOrWhiteSpace(connection.AccessTokenEncrypted)
            || string.IsNullOrWhiteSpace(connection.PhoneNumberId) || string.IsNullOrWhiteSpace(connection.WhatsAppBusinessAccountId))
            throw new ConflictException("Save the account IDs and access token in this WhatsApp connection's setup before sending.");
        string accessToken;
        try { accessToken = protector.Unprotect(connection.AccessTokenEncrypted); }
        catch (CryptographicException)
        {
            throw new ConflictException("The saved access token cannot be read. Save a replacement token in this connection's setup.");
        }
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ConflictException("Save a replacement access token in this connection's setup.");
        MessageDocument? latest = await store.LatestIncomingAsync(owner, connectionId, recipient, ct);

        // SentAt is absent on documents written before it existed; ReceivedAt is within seconds of it.
        if (latest is null || (latest.SentAt ?? latest.ReceivedAt) < DateTime.UtcNow.AddHours(-24))
            throw new ConflictException("Ask this customer to message your business first. Text replies require an incoming message within 24 hours.");

        // The reply joins the customer's conversation, so it takes that message's
        // key and business number rather than rebuilding them from the
        // connection, whose number was typed in at setup and may be written
        // differently. A message stored before the worker has no key; the reply
        // then has none either, as before.
        string? conversationKey = latest.ConversationKey;
        long? replySequence = conversationKey is null ? null : await sequence.NextAsync(conversationKey, ct);

        DateTime now = DateTime.UtcNow;
        WhatsAppMessageDocument doc = new WhatsAppMessageDocument
        {
            MessageId = RequestKey(owner, request.RequestId), OwnerUserId = owner, ConnectionId = connectionId,
            Platform = MessagingPlatform.WhatsApp, ExternalAccountId = connection.WhatsAppBusinessAccountId!,
            ChannelId = connection.PhoneNumberId!, ChannelDisplayName = connection.DisplayPhoneNumber ?? "",
            // Outgoing replies never pass through a webhook, so SentAt is our own clock.
            From = recipient, Timestamp = new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), SentAt = now,
            Type = "text", TextBody = request.Text, ReceivedAt = now, StoredAt = now,
            Outgoing = true, SendStatus = "pending",
            ConversationKey = conversationKey, Sequence = replySequence, Receiver = latest.Receiver,
        };
        if (!await store.ReserveAsync(doc, ct))
        {
            existing = await store.FindRequestAsync(owner, request.RequestId, ct)
                ?? throw new ConflictException("The send request is being processed. Check again shortly.");
            return Match(existing, connectionId, recipient, request.Text);
        }

        // Once reserved, browser disconnection must not cancel an already dispatched send.
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string status;
        string? providerId = null;
        string metaErrorCode = "none";
        try
        {
            providerId = await sender.SendAsync(connection.PhoneNumberId!, accessToken, recipient, request.Text, timeout.Token);
            status = "accepted";

            // From the customer's message arriving to Meta accepting the reply.
            MessageMetrics.EndToEnd.Record(Math.Max(0, (DateTime.UtcNow - latest.ReceivedAt).TotalSeconds));
        }
        catch (WhatsAppRejectedException ex)
        {
            status = "failed";
            metaErrorCode = ex.MetaErrorCode ?? "unknown";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            status = "unknown";
        }
        MessageMetrics.Sent.Add(
            1,
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("meta_error_code", metaErrorCode));
        using CancellationTokenSource persistTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await store.CompleteAsync(doc.MessageId, status, providerId, persistTimeout.Token);
        doc.SendStatus = status;
        doc.ProviderMessageId = providerId;
        return ToMessage(doc);
    }

    public static string RequestKey(string owner, Guid request) => $"outgoing:{owner}:{request:D}";
    public static ConversationMessage ToMessage(MessageDocument doc) => new(doc.MessageId,
        doc.TextBody ?? $"[{doc.Type} message]", doc.Outgoing, doc.SendStatus, doc.ReceivedAt);

    private static ConversationMessage Match(MessageDocument doc, Guid connection, string recipient, string text)
    {
        if (doc.ConnectionId != connection || doc.From != recipient || doc.TextBody != text)
            throw new ConflictException("This request ID has already been used for a different message.");
        return ToMessage(doc);
    }
}
