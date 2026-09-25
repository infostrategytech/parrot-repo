using MongoDB.Bson.Serialization.Attributes;

namespace Parrot.Domain.Entities.Messages;

[BsonDiscriminator("facebook")]
public sealed class FacebookMessageDocument : MessageDocument
{
    // Messenger reports an attachment's type in Type but keeps the payload separate; without this the
    // URL is lost and an "image" message can never be retrieved. Meta signs these URLs and they expire,
    // so treat them as a pointer to fetch and re-host, not as durable storage.
    public List<string>? AttachmentUrls { get; set; }
}
