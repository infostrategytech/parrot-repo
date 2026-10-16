using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

namespace Parrot.Application.Conversations;

public record ConversationSummary(Guid ConnectionId, string Sender, string Name, string Preview, DateTime UpdatedAt)
{
    /// <summary>whatsapp, facebook, tiktok or instagram: which channel the conversation is on.</summary>
    public string Channel { get; init; } = "whatsapp";

    /// <summary>Customer messages the signed-in user has not read. Zero for a read conversation.</summary>
    public int UnreadCount { get; init; }

    /// <summary>When the customer last wrote, or null when they never have. Starts the reply window.</summary>
    public DateTime? LastCustomerMessageAt { get; init; }

    /// <summary>How long after the customer's last message the business may reply: 24 hours, or 48 on TikTok.</summary>
    public int ReplyWindowHours { get; init; }

    /// <summary>The business's account on that channel (the Facebook Page id, the WhatsApp business account...), from the latest message.</summary>
    public string? AccountId { get; init; }

    /// <summary>The customer's handle on the channel without the @ (Instagram), when the channel gave one.</summary>
    public string? Username { get; init; }

    /// <summary>The signed-in user pinned this conversation: it is listed above the others.</summary>
    public bool Pinned { get; init; }

    /// <summary>The signed-in user marked it unread to come back to: the inbox shows "!" when no message is unread.</summary>
    public bool MarkedUnread { get; init; }
}
public record ConversationMessage(string Id, string Text, bool Outgoing, string Status, DateTime ReceivedAt)
{
    /// <summary>The files the customer sent with it, or null when it had none.</summary>
    public IReadOnlyList<MessageMedia>? Attachments { get; init; }

    /// <summary>The message this one replies to (quotes), or null when it quotes nothing.</summary>
    public MessageQuote? ReplyTo { get; init; }
}

/// <summary>
/// A quoted message, as shown above a reply. <see cref="Id"/> is the quoted message's
/// <see cref="ConversationMessage.Id"/>, null when it is not in the inbox (older than it, or unknown).
/// <see cref="Type"/> says what it was when it has no text ("image", "document", …).
/// </summary>
public record MessageQuote(string? Id, bool Outgoing, string? Text, string? Type);

/// <summary>
/// One file of a message as the inbox reads it. <see cref="Url"/> is a short-lived signed address the browser
/// can load directly (or, for a link, the page to open); null when the file could not be saved.
/// </summary>
public record MessageMedia(string Kind, string? ContentType, string? FileName, long? SizeBytes, string? Url)
{
    /// <summary>pending, ready, failed or too_large (see MediaStatus). Ready is the default for a file that has a Url.</summary>
    public string Status { get; init; } = Parrot.Domain.Entities.Messages.MediaStatus.Ready;

    /// <summary>Where the private copy lives. Server side only: swapped for <see cref="Url"/> before it leaves the API.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? StorageKey { get; init; }
}

/// <summary>A file in the customer's media list, with the message that carried it.</summary>
public record MediaItem(string MessageId, DateTime ReceivedAt, MessageMedia Media);

public record MediaPage(IReadOnlyList<MediaItem> Items, string? NextBefore);
public record SendReplyRequest(Guid RequestId, string Text)
{
    /// <summary>The <see cref="ConversationMessage.Id"/> of the message this replies to, quoting it; null for a plain reply.</summary>
    public string? ReplyToMessageId { get; init; }
}

/// <summary>A file a teammate attached to a reply. <see cref="Open"/> reads it from the start each time it is called.</summary>
public sealed record OutgoingFile(string FileName, string ContentType, long Length, Func<Stream> Open);
public record MessagePage(IReadOnlyList<ConversationMessage> Messages, string? NextBefore);

/// <summary>Unread customer messages across every conversation and channel.</summary>
public record UnreadSummary(int Total);

/// <summary>UpToMessageId is the id (ConversationMessage.Id) of the newest message the agent has seen in the thread.</summary>
public record MarkReadRequest(string UpToMessageId);

/// <summary>What is still unread after a mark: in that conversation, and in total.</summary>
public record MarkReadResult(int ConversationUnread, int Total);

/// <summary>
/// A read that moved the user's position forward: the customer's newest message they have now seen, which is
/// what the customer's channel is told was read.
/// </summary>
public sealed record ReadAdvance(Guid Connection, string Sender, MessagingPlatform Platform, string MessageId);

/// <summary>
/// Which customer messages a user has read. A user's position in a conversation is
/// the newest message they have seen; unread is every incoming message after it, on
/// any channel. The agent's own replies never count.
/// </summary>
public interface IConversationReadStore
{
    /// <summary>
    /// Moves the user's position forward to the given message (by its ConversationMessage.Id) of that conversation. A
    /// position never moves back, so a late or repeated call cannot un-read anything. Returns what the move newly
    /// read, or null when it moved nothing (a repeat, or an older message).
    /// </summary>
    Task<ReadAdvance?> MarkReadAsync(string owner, string reader, Guid connection, string sender, string upToMessageId, CancellationToken ct);

    /// <summary>Unread counts for the given conversations. A conversation with none is absent.</summary>
    Task<IReadOnlyDictionary<(Guid Connection, string Sender), int>> CountUnreadAsync(
        string owner, string reader, IReadOnlyCollection<(Guid Connection, string Sender)> conversations, CancellationToken ct);

    /// <summary>Unread messages across all of the owner's conversations.</summary>
    Task<int> TotalUnreadAsync(string owner, string reader, CancellationToken ct);
}

/// <summary>Sets or clears one of the per-user conversation flags.</summary>
public record SetPinnedRequest(bool Pinned);

/// <summary>Sets or clears one of the per-user conversation flags.</summary>
public record SetMarkedUnreadRequest(bool MarkedUnread);

/// <summary>A user's own marks on a conversation. They are not shared with teammates or the customer.</summary>
public sealed record ConversationFlags(bool Pinned, DateTime? PinnedAt, bool MarkedUnread);

/// <summary>
/// What a user has pinned or marked unread. Stored per user and conversation, so a pin survives
/// reloads and follows the user to another device.
/// </summary>
public interface IConversationFlagStore
{
    /// <summary>Every conversation the user has pinned or marked unread. Conversations with neither are absent.</summary>
    Task<IReadOnlyDictionary<(Guid Connection, string Sender), ConversationFlags>> ListAsync(string reader, CancellationToken ct);

    Task SetPinnedAsync(string reader, Guid connection, string sender, bool pinned, CancellationToken ct);

    Task SetMarkedUnreadAsync(string reader, Guid connection, string sender, bool markedUnread, CancellationToken ct);
}

public interface IConversationStore
{
    Task<IReadOnlyList<ConversationSummary>> ListAsync(string owner, int skip, int take, CancellationToken ct);

    /// <summary>The summaries of just these conversations (those with no messages are absent), newest first.</summary>
    Task<IReadOnlyList<ConversationSummary>> FindAsync(string owner, IReadOnlyCollection<(Guid Connection, string Sender)> conversations, CancellationToken ct);
    Task<MessagePage> ReadAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct);

    /// <summary>The files this customer sent, newest first, a page at a time. Pass the previous page's NextBefore for the next.</summary>
    Task<MediaPage> ListMediaAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct);

    // The customer's most recent message on this connection, or null when there is none.
    Task<MessageDocument?> LatestIncomingAsync(string owner, Guid connection, string sender, CancellationToken ct);
    Task<MessageDocument?> FindRequestAsync(string owner, Guid request, CancellationToken ct);

    /// <summary>One message of this conversation by its <see cref="ConversationMessage.Id"/>, or null.</summary>
    Task<MessageDocument?> FindMessageAsync(string owner, Guid connection, string sender, string messageId, CancellationToken ct) =>
        Task.FromResult<MessageDocument?>(null);
    Task<bool> ReserveAsync(MessageDocument message, CancellationToken ct);
    Task CompleteAsync(string id, string status, string? providerId, CancellationToken ct);

    /// <summary>
    /// Marks a reply failed only while it is still pending: false when the send worker has already taken it, in
    /// which case its outcome is the worker's to record and must not be overwritten.
    /// </summary>
    Task<bool> FailPendingAsync(string id, CancellationToken ct);
}

// When a reply was sent, found by the wamid Meta gave it: for delivery latency
// when Meta later reports the reply delivered or read. Null when unknown.
public interface IReplySendTimes
{
    Task<DateTime?> SentAtAsync(string providerMessageId, CancellationToken ct);
}

public interface IWhatsAppSender
{
    Task<string> SendAsync(string phoneNumberId, string accessToken, string recipient, string text, CancellationToken ct);

    /// <summary>Uploads the file to WhatsApp and sends it as an image or document, with the caption when there is one.</summary>
    Task<string> SendMediaAsync(string phoneNumberId, string accessToken, string recipient, string kind, string contentType,
        string fileName, Stream content, string? caption, CancellationToken ct);

    /// <summary>As <see cref="SendAsync"/>, quoting the message WhatsApp knows by <paramref name="replyTo"/> (its wamid).</summary>
    Task<string> SendReplyAsync(string phoneNumberId, string accessToken, string recipient, string text, string replyTo, CancellationToken ct);

    /// <summary>As <see cref="SendMediaAsync"/>, quoting the message WhatsApp knows by <paramref name="replyTo"/>.</summary>
    Task<string> SendMediaReplyAsync(string phoneNumberId, string accessToken, string recipient, string kind, string contentType,
        string fileName, Stream content, string? caption, string replyTo, CancellationToken ct);

    /// <summary>Tells WhatsApp the business has read this incoming message (the customer sees blue ticks), and every earlier one.</summary>
    Task MarkReadAsync(string phoneNumberId, string accessToken, string messageId, CancellationToken ct);
}

// Messenger's Send API, with the Page access token saved in setup. Returns Meta's message id (m_…).
public interface IFacebookSender
{
    Task<string> SendAsync(string pageId, string pageAccessToken, string recipient, string text, CancellationToken ct);

    /// <summary>Uploads the file with the message and sends it as an image or file. Messenger has no caption.</summary>
    Task<string> SendMediaAsync(string pageId, string pageAccessToken, string recipient, string kind, string contentType,
        string fileName, Stream content, CancellationToken ct);

    /// <summary>As <see cref="SendAsync"/>, quoting the message Messenger knows by <paramref name="replyTo"/> (its mid).</summary>
    Task<string> SendReplyAsync(string pageId, string pageAccessToken, string recipient, string text, string replyTo, CancellationToken ct);

    /// <summary>As <see cref="SendMediaAsync"/>, quoting the message Messenger knows by <paramref name="replyTo"/>.</summary>
    Task<string> SendMediaReplyAsync(string pageId, string pageAccessToken, string recipient, string kind, string contentType,
        string fileName, Stream content, string replyTo, CancellationToken ct);

    /// <summary>Shows the customer the Page has seen their latest message.</summary>
    Task MarkSeenAsync(string pageId, string pageAccessToken, string recipient, CancellationToken ct);
}

public sealed class FacebookRejectedException : Exception
{
    public FacebookRejectedException(string? metaErrorCode = null)
        : base("Messenger rejected the message. Check the Page access token and that the customer wrote within 24 hours.") =>
        MetaErrorCode = metaErrorCode;

    // Meta's error.code (e.g. "10" with subcode 2018278 outside the 24-hour window, "551" when the person is unavailable).
    public string? MetaErrorCode { get; }
}

// Instagram's Send API (graph.instagram.com/{account}/messages) with the account's token.
public interface IInstagramSender
{
    Task<string> SendAsync(string accountId, string accessToken, string recipient, string text, CancellationToken ct);

    /// <summary>As <see cref="SendAsync"/>, quoting the message Instagram knows by <paramref name="replyTo"/> (its mid).</summary>
    Task<string> SendReplyAsync(string accountId, string accessToken, string recipient, string text, string replyTo, CancellationToken ct);

    /// <summary>Shows the customer the account has seen their latest message.</summary>
    Task MarkSeenAsync(string accountId, string accessToken, string recipient, CancellationToken ct);
}

public sealed class InstagramRejectedException : Exception
{
    public InstagramRejectedException(string? metaErrorCode = null)
        : base("Instagram rejected the message. Check the access token and that the customer wrote within 24 hours.") =>
        MetaErrorCode = metaErrorCode;

    // Meta's error.code (e.g. "10" with subcode 2534022 outside the 24-hour window).
    public string? MetaErrorCode { get; }
}

public sealed class WhatsAppRejectedException : Exception
{
    public WhatsAppRejectedException(string? metaErrorCode = null)
        : base("WhatsApp rejected the message. Check the recipient, access token and messaging permissions.") =>
        MetaErrorCode = metaErrorCode;

    // Meta's error.code (e.g. "131047", "130429"), when the response carried one.
    public string? MetaErrorCode { get; }
}
