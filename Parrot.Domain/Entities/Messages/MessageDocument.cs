using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Parrot.Domain.Enums;

namespace Parrot.Domain.Entities.Messages;

// Single Collection Inheritance: every platform's messages share one MongoDB collection and are told
// apart by the "_t" discriminator. Fields common to all platforms belong here; only genuinely
// platform-specific data belongs on a subclass.
// BsonIgnoreExtraElements is inherited so that an older process can still read documents written by a
// newer one — the API and the message worker deploy independently and both read this collection.
[BsonDiscriminator(Required = true)]
[BsonKnownTypes(typeof(WhatsAppMessageDocument), typeof(FacebookMessageDocument), typeof(TikTokMessageDocument), typeof(InstagramMessageDocument))]
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

    // When a send worker took this reply to hand it to the channel (status "sending"). A reply still "sending"
    // long after this was lost mid-send: whether the customer got it is unknown, so it is never sent again.
    [BsonIgnoreIfNull]
    public DateTime? SendClaimedAt { get; set; }

    // When the channel reported our reply delivered to the customer's device, and when the customer read
    // it. Set from the channel's receipts, only on replies, and only ever to the earliest time reported.
    // A customer who has turned read receipts off never gets a ReadAt: absent does not mean unread.
    [BsonIgnoreIfNull]
    public DateTime? DeliveredAt { get; set; }

    [BsonIgnoreIfNull]
    public DateTime? ReadAt { get; set; }

    public required string MessageId { get; set; }
    public required MessagingPlatform Platform { get; set; }
    public required string ExternalAccountId { get; set; }
    public required string From { get; set; }
    // Raw provider value: WhatsApp sends seconds, Messenger milliseconds. Compare SentAt instead.
    public required string Timestamp { get; set; }

    // Timestamp normalised to UTC. Null on documents written before this field existed.
    public DateTime? SentAt { get; set; }
    public required string Type { get; set; }
    public string? TextBody { get; set; }
    public string? ContactName { get; set; }
    // Left out when the channel gave none, so a message without one stores the fields it always did.
    [BsonIgnoreIfNull]
    public string? ContactUsername { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime StoredAt { get; set; }

    // The account the message belongs to: a WhatsApp phone number ID, or a Facebook Page ID.
    // Both platforms carry exactly these two, and every index and query uses them, so they live on the base.
    public required string ChannelId { get; set; }
    public required string ChannelDisplayName { get; set; }

    // Written by the message worker; absent on documents stored before it, and on
    // outgoing replies until those are given one. Left out when null so the
    // unique (ConversationKey, Sequence) index only ever sees real values.

    // See Parrot.Application.Messaging.ConversationKey: one key for both directions.
    [BsonIgnoreIfNull]
    public string? ConversationKey { get; set; }

    // Position within the conversation, from a per-conversation counter. It may
    // skip numbers (an insert that failed after the increment); compare, don't count.
    [BsonIgnoreIfNull]
    public long? Sequence { get; set; }

    // The other participant, normalized like the sender: the business number for
    // an incoming WhatsApp message, the Page id for Messenger.
    [BsonIgnoreIfNull]
    public string? Receiver { get; set; }

    // The files the customer sent with this message, on any platform. Left out when there are none, so
    // the sparse index the media listing uses only holds messages that have some.
    [BsonIgnoreIfNull]
    public List<MessageAttachment>? Attachments { get; set; }

    // The message this one replies to, in either direction. Left out when it quotes nothing.
    [BsonIgnoreIfNull]
    public MessageReplyTo? ReplyTo { get; set; }

    // True when the reply was sent by an AI agent, not a human teammate. Left out for all other messages so
    // older documents (which predate this field) remain readable without change.
    [BsonIgnoreIfNull]
    public bool? IsAiReply { get; set; }
}
