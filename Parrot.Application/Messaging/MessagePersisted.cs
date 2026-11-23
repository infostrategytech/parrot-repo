using Parrot.Domain.Enums;

namespace Parrot.Application.Messaging;

/// <summary>
/// Published to the persisted topic once a message is safely in MongoDB, for
/// realtime delivery to whoever is online. Delivery is best-effort: a client
/// that misses one recovers it from <see cref="IMessageHistory"/>.
///
/// It can arrive more than once (a replay after a crash publishes it again),
/// so consumers deduplicate on <see cref="StoredMessage.MessageId"/>.
/// </summary>
public sealed record MessagePersisted
{
    public int SchemaVersion { get; init; } = 2;

    /// <summary>Where the message came from. Written as text on the wire, e.g. "whatsapp".</summary>
    required public MessagingPlatform Source { get; init; }

    /// <summary>The message as stored.</summary>
    required public StoredMessage Data { get; init; }

    /// <summary>The trace id of the webhook request that brought the message in.</summary>
    required public string CorrelationId { get; init; }
}

/// <summary>
/// A message as MongoDB holds it and as the CRM shows it. The realtime event
/// and the history API both carry this one shape, so a client reconciles the
/// two by <see cref="MessageId"/> and orders by <see cref="Sequence"/>.
/// </summary>
public sealed record StoredMessage
{
    required public string MessageId { get; init; }

    required public string ConversationKey { get; init; }

    /// <summary>
    /// Position within the conversation, increasing with every stored message.
    /// It may skip numbers, so compare it rather than count with it. This is
    /// the history cursor.
    /// </summary>
    required public long Sequence { get; init; }

    required public MessagingPlatform Platform { get; init; }

    /// <summary>The business account the message belongs to.</summary>
    public Guid? ConnectionId { get; init; }

    /// <summary>The CRM user to deliver it to.</summary>
    public string? OwnerUserId { get; init; }

    /// <summary>The specific business workspace that owns this conversation.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The root user account that owns the workspace.</summary>
    public string? TenantId { get; init; }

    /// <summary>True for a reply the business sent, false for a customer's message.</summary>
    required public bool Outgoing { get; init; }

    required public string Sender { get; init; }

    required public string Receiver { get; init; }

    /// <summary>The customer's display name, when the provider sent one.</summary>
    public string? SenderName { get; init; }

    /// <summary>The provider's message type, e.g. "text" or "image".</summary>
    required public string Type { get; init; }

    public string? Text { get; init; }

    public IReadOnlyList<string>? AttachmentUrls { get; init; }

    /// <summary>
    /// The files the message carries. On the event each has a download link for readers outside the inbox (the AI
    /// team); the inbox ignores it and reads each file through its own signed address.
    /// </summary>
    public IReadOnlyList<StoredAttachment>? Attachments { get; init; }

    public DateTime? SentAt { get; init; }

    required public DateTime PersistedAt { get; init; }

    /// <summary>The message this one replies to (quotes), or null when it quotes nothing.</summary>
    public StoredQuote? ReplyTo { get; init; }

    /// <summary>The direction and intent of the message relative to the AI agent.</summary>
    public CommsType CommsType { get; init; } = CommsType.UserToTenant;
}

/// <param name="Kind">See <see cref="MediaKinds"/>.</param>
/// <param name="Url">
/// A link anyone can download the file from for a week, without signing in (a shared post's own address, for a link).
/// Null when there is no saved file (pending, failed, too large), and in the history API, which makes no links.
/// </param>
/// <param name="FileName">The name the file came with, or one made from its kind and time (image-20261123-132153.jpg). Null only for a link.</param>
/// <param name="Available">False when the file cannot be read now: still pending, too large, or the platform refused it.</param>
/// <param name="Status">pending, ready, failed or too_large (see MediaStatus).</param>
public sealed record StoredAttachment(string Kind, string? Url, string? ContentType, string? FileName, long? SizeBytes, bool Available,
    string Status = Parrot.Domain.Entities.Messages.MediaStatus.Ready);

/// <summary>
/// The message a stored message quotes: the inbox's <see cref="Conversations.MessageQuote"/> fields, plus
/// <see cref="Url"/>, a week-long download link to the quoted message's file (null when it had none, or in the
/// history API). Kept apart from MessageQuote so the inbox's own replies do not change.
/// </summary>
public sealed record StoredQuote(string? Id, bool Outgoing, string? Text, string? Type, string? Url = null);

/// <summary>
/// Pushed to the owner's inbox when a channel reports receipts for replies in a conversation. It names the
/// conversation, not the receipts: the inbox re-reads the thread it has open.
/// </summary>
/// <param name="Customer">Null when the channel did not say which customer, which means any on that connection.</param>
public sealed record ReceiptsChanged(Guid ConnectionId, string? Customer)
{
    /// <summary>True when this is about the thread the inbox is showing.</summary>
    public bool Concerns(Guid connection, string sender) =>
        ConnectionId == connection && (Customer is null || Customer == sender);
}
