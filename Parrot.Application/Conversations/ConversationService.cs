using System.Security.Cryptography;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Conversations;

public sealed class ConversationService(IConversationStore store, IWhatsAppConnectionRepository connections,
    IWhatsAppSender sender, IWhatsAppTokenProtector protector, IConversationSequence sequence,
    IFacebookConnectionRepository facebookConnections, IFacebookSender facebookSender,
    IFacebookCredentialProtector facebookProtector)
{
    // Messenger's limit; WhatsApp's is 4096.
    private const int FacebookMaxText = 2000;

    // A reply goes out on whichever channel the thread is on: a WhatsApp number or a Facebook Page.
    public async Task<ConversationMessage> SendAsync(string owner, Guid connectionId, string recipient,
        SendReplyRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 4096
            || recipient.Length is < 1 or > 32 || !recipient.All(char.IsAsciiDigit))
            throw new ValidationException("Supply a request ID, a numeric recipient and text of 1–4096 characters.");

        WhatsAppConnection? whatsApp = await connections.GetByOwnerAndIdAsync(owner, connectionId);
        if (whatsApp is not null)
        {
            if (recipient.Length > 20)
                throw new ValidationException("Supply a request ID, a numeric recipient and text of 1–4096 characters.");
            return await SendWhatsAppAsync(owner, whatsApp, recipient, request, ct);
        }

        FacebookConnection? page = await facebookConnections.GetByOwnerAsync(owner, ct);
        if (page is not null && page.Id == connectionId)
        {
            if (request.Text.Length > FacebookMaxText)
                throw new ValidationException($"Messenger replies can be up to {FacebookMaxText} characters.");
            return await SendFacebookAsync(owner, page, recipient, request, ct);
        }

        throw new NotFoundException("Connection not found.");
    }

    private async Task<ConversationMessage> SendWhatsAppAsync(string owner, WhatsAppConnection connection, string recipient,
        SendReplyRequest request, CancellationToken ct)
    {
        if (connection.DisabledAt.HasValue || connection.WebhookVerifiedAt is null)
            throw new ConflictException("This connection is disabled or unverified.");

        MessageDocument? existing = await store.FindRequestAsync(owner, request.RequestId, ct);
        if (existing is not null) return Match(existing, connection.Id, recipient, request.Text);

        if (string.IsNullOrWhiteSpace(connection.AccessTokenEncrypted)
            || string.IsNullOrWhiteSpace(connection.PhoneNumberId) || string.IsNullOrWhiteSpace(connection.WhatsAppBusinessAccountId))
            throw new ConflictException("Save the account IDs and access token in this WhatsApp connection's setup before sending.");
        string accessToken = Unprotect(() => protector.Unprotect(connection.AccessTokenEncrypted));
        (MessageDocument latest, string? conversationKey, long? replySequence) = await PrepareAsync(owner, connection.Id, recipient, ct);

        DateTime now = DateTime.UtcNow;
        WhatsAppMessageDocument doc = new WhatsAppMessageDocument
        {
            MessageId = RequestKey(owner, request.RequestId), OwnerUserId = owner, ConnectionId = connection.Id,
            Platform = MessagingPlatform.WhatsApp, ExternalAccountId = connection.WhatsAppBusinessAccountId!,
            ChannelId = connection.PhoneNumberId!, ChannelDisplayName = connection.DisplayPhoneNumber ?? "",
            // Outgoing replies never pass through a webhook, so SentAt is our own clock.
            From = recipient, Timestamp = new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), SentAt = now,
            Type = "text", TextBody = request.Text, ReceivedAt = now, StoredAt = now,
            Outgoing = true, SendStatus = "pending",
            ConversationKey = conversationKey, Sequence = replySequence, Receiver = latest.Receiver,
        };

        return await ReserveAndSendAsync(owner, doc, request, latest, async timeout =>
        {
            try
            {
                return (await sender.SendAsync(connection.PhoneNumberId!, accessToken, recipient, request.Text, timeout), null);
            }
            catch (WhatsAppRejectedException ex)
            {
                return (null, ex.MetaErrorCode ?? "unknown");
            }
        }, ct);
    }

    private async Task<ConversationMessage> SendFacebookAsync(string owner, FacebookConnection page, string recipient,
        SendReplyRequest request, CancellationToken ct)
    {
        if (page.DisabledAt.HasValue || page.WebhookVerifiedAt is null)
            throw new ConflictException("This connection is disabled or unverified.");

        MessageDocument? existing = await store.FindRequestAsync(owner, request.RequestId, ct);
        if (existing is not null) return Match(existing, page.Id, recipient, request.Text);

        if (string.IsNullOrWhiteSpace(page.AccessTokenEncrypted) || string.IsNullOrWhiteSpace(page.PageId))
            throw new ConflictException("Save the Page and its access token in this Facebook connection's setup before sending.");
        string pageToken = Unprotect(() => facebookProtector.Unprotect(page.AccessTokenEncrypted));
        (MessageDocument latest, string? conversationKey, long? replySequence) = await PrepareAsync(owner, page.Id, recipient, ct);

        DateTime now = DateTime.UtcNow;
        FacebookMessageDocument doc = new FacebookMessageDocument
        {
            MessageId = RequestKey(owner, request.RequestId), OwnerUserId = owner, ConnectionId = page.Id,
            Platform = MessagingPlatform.Facebook, ExternalAccountId = page.PageId!,
            ChannelId = page.PageId!, ChannelDisplayName = page.PageName ?? "",
            // From is the customer for both directions, as the inbox groups by it. Messenger counts in milliseconds.
            From = recipient, Timestamp = new DateTimeOffset(now).ToUnixTimeMilliseconds().ToString(), SentAt = now,
            Type = "text", TextBody = request.Text, ReceivedAt = now, StoredAt = now,
            Outgoing = true, SendStatus = "pending",
            ConversationKey = conversationKey, Sequence = replySequence, Receiver = latest.Receiver ?? page.PageId,
        };

        return await ReserveAndSendAsync(owner, doc, request, latest, async timeout =>
        {
            try
            {
                return (await facebookSender.SendAsync(page.PageId!, pageToken, recipient, request.Text, timeout), null);
            }
            catch (FacebookRejectedException ex)
            {
                return (null, ex.MetaErrorCode ?? "unknown");
            }
        }, ct);
    }

    // The customer's latest message: it must be within 24 hours (WhatsApp's and Messenger's rule
    // for a free-form reply), and the reply joins its conversation. The reply takes that message's
    // key and business id rather than rebuilding them from the connection, whose number was typed
    // in at setup and may be written differently. A message stored before the worker has no key;
    // the reply then has none either.
    private async Task<(MessageDocument Latest, string? ConversationKey, long? Sequence)> PrepareAsync(
        string owner, Guid connectionId, string recipient, CancellationToken ct)
    {
        MessageDocument? latest = await store.LatestIncomingAsync(owner, connectionId, recipient, ct);

        // SentAt is absent on documents written before it existed; ReceivedAt is within seconds of it.
        if (latest is null || (latest.SentAt ?? latest.ReceivedAt) < DateTime.UtcNow.AddHours(-24))
            throw new ConflictException("Ask this customer to message your business first. Text replies require an incoming message within 24 hours.");

        string? conversationKey = latest.ConversationKey;
        long? replySequence = conversationKey is null ? null : await sequence.NextAsync(conversationKey, ct);
        return (latest, conversationKey, replySequence);
    }

    // Reserved before the provider is called, so concurrent requests with one request id send once.
    private async Task<ConversationMessage> ReserveAndSendAsync(string owner, MessageDocument doc, SendReplyRequest request,
        MessageDocument latest, Func<CancellationToken, Task<(string? ProviderId, string? RejectedCode)>> send,
        CancellationToken ct)
    {
        if (!await store.ReserveAsync(doc, ct))
        {
            MessageDocument existing = await store.FindRequestAsync(owner, request.RequestId, ct)
                ?? throw new ConflictException("The send request is being processed. Check again shortly.");
            return Match(existing, doc.ConnectionId!.Value, doc.From, request.Text);
        }

        // Once reserved, browser disconnection must not cancel an already dispatched send.
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string status;
        string? providerId = null;
        string metaErrorCode = "none";
        try
        {
            (providerId, string? rejectedCode) = await send(timeout.Token);
            if (rejectedCode is null)
            {
                status = "accepted";

                // From the customer's message arriving to Meta accepting the reply.
                MessageMetrics.EndToEnd.Record(Math.Max(0, (DateTime.UtcNow - latest.ReceivedAt).TotalSeconds));
            }
            else
            {
                status = "failed";
                metaErrorCode = rejectedCode;
            }
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

    private static string Unprotect(Func<string> unprotect)
    {
        string token;
        try { token = unprotect(); }
        catch (CryptographicException)
        {
            throw new ConflictException("The saved access token cannot be read. Save a replacement token in this connection's setup.");
        }
        if (string.IsNullOrWhiteSpace(token))
            throw new ConflictException("Save a replacement access token in this connection's setup.");
        return token;
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
