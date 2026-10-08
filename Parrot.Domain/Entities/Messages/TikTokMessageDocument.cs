using MongoDB.Bson.Serialization.Attributes;

namespace Parrot.Domain.Entities.Messages;

[BsonDiscriminator("tiktok")]
public sealed class TikTokMessageDocument : MessageDocument
{
    // Media URLs of the message, kept as FacebookMessageDocument keeps them.
    public List<string>? AttachmentUrls { get; set; }
}
