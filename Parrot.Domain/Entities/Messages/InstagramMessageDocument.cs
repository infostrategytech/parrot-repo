using MongoDB.Bson.Serialization.Attributes;

namespace Parrot.Domain.Entities.Messages;

[BsonDiscriminator("instagram")]
public sealed class InstagramMessageDocument : MessageDocument
{
    // As on Messenger: an attachment's type is in Type and its URL here. Meta signs these URLs and
    // they expire, so treat them as a pointer to fetch and re-host, not as durable storage.
    public List<string>? AttachmentUrls { get; set; }
}
