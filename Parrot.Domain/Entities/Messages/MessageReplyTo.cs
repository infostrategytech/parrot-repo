using MongoDB.Bson.Serialization.Attributes;

namespace Parrot.Domain.Entities.Messages;

/// <summary>
/// The message this one replies to (quotes), as WhatsApp Web shows it above a reply: either side can
/// reply to either side's message. Kept as a small copy taken when the reply was stored, so the quote
/// shows without reading the original, even when the original is older than the thread's first page.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class MessageReplyTo
{
    /// <summary>The channel's own id for the quoted message (a wamid, a Messenger or Instagram mid). Always set.</summary>
    public required string ProviderMessageId { get; set; }

    /// <summary>The quoted message's id in the inbox (its MessageId), when it was found among the stored messages.</summary>
    [BsonIgnoreIfNull]
    public string? MessageId { get; set; }

    /// <summary>Whether the business sent the quoted message (a reply) rather than the customer.</summary>
    public bool Outgoing { get; set; }

    /// <summary>The quoted message's text or caption, when it had one.</summary>
    [BsonIgnoreIfNull]
    public string? Text { get; set; }

    /// <summary>The quoted message's type ("text", "image", "document", …), so a quote without text can say what it was.</summary>
    [BsonIgnoreIfNull]
    public string? Type { get; set; }

    /// <summary>Where the quoted message's file is saved, so the persisted event can link to it. Null when it had none.</summary>
    [BsonIgnoreIfNull]
    public string? StorageKey { get; set; }
}
