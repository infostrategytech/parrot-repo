using MongoDB.Bson.Serialization.Attributes;

namespace Parrot.Domain.Entities.Messages;

[BsonDiscriminator("tiktok")]
public sealed class TikTokMessageDocument : MessageDocument
{
    // TikTok's id for the conversation; replying to the customer is addressed to it.
    public string? ConversationId { get; set; }

    // Media URLs of the message, kept as FacebookMessageDocument keeps them.
    public List<string>? AttachmentUrls { get; set; }
}
