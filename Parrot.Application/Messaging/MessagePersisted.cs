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
    public int SchemaVersion { get; init; } = 1;

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

    /// <summary>The files the customer sent. Describes them only: the inbox reads each one through the API, which checks who is asking.</summary>
    public IReadOnlyList<StoredAttachment>? Attachments { get; init; }

    public DateTime? SentAt { get; init; }

    required public DateTime PersistedAt { get; init; }

    /// <summary>The message this one replies to (quotes), or null when it quotes nothing.</summary>
    public Parrot.Application.Conversations.MessageQuote? ReplyTo { get; init; }
}

/// <param name="Kind">See <see cref="MediaKinds"/>.</param>
/// <param name="Available">False when the file could not be saved (too large, or the platform refused it).</param>
public sealed record StoredAttachment(string Kind, string? ContentType, string? FileName, long? SizeBytes, bool Available);

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
