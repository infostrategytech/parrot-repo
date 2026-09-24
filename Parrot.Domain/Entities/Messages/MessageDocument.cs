using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Parrot.Domain.Enums;

namespace Parrot.Domain.Entities.Messages;

// Single Collection Inheritance: every platform's messages share one MongoDB collection and are told
// apart by the "_t" discriminator. Fields common to all platforms belong here; only genuinely
// platform-specific data belongs on a subclass.
// BsonIgnoreExtraElements is inherited so that an older process can still read documents written by a
// newer one — the API and the ingestor deploy independently and both read this collection.
[BsonDiscriminator(Required = true)]
[BsonKnownTypes(typeof(WhatsAppMessageDocument), typeof(FacebookMessageDocument))]
[BsonIgnoreExtraElements(Inherited = true)]
public abstract class MessageDocument
{
    [BsonRepresentation(BsonType.String)]
    public Guid? ConnectionId { get; set; }
    public string? OwnerUserId { get; set; }
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    public bool Outgoing { get; set; }
    public string SendStatus { get; set; } = "received";
    public string? ProviderMessageId { get; set; }

    public required string MessageId { get; set; }
    public required MessagingPlatform Platform { get; set; }
    public required string ExternalAccountId { get; set; }
    public required string From { get; set; }
    public required string Timestamp { get; set; }
    public required string Type { get; set; }
    public string? TextBody { get; set; }
    public string? ContactName { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime StoredAt { get; set; }

    // The account the message belongs to: a WhatsApp phone number ID, or a Facebook Page ID.
    // Both platforms carry exactly these two, and every index and query uses them, so they live on the base.
    public required string ChannelId { get; set; }
    public required string ChannelDisplayName { get; set; }
}
