using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class MessageDocumentMappingTests
{
    private static MessageDocumentMapperFactory NewFactory() =>
        new([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper()]);

    private static IncomingMessage NewMessage(MessagingPlatform platform) => new()
    {
        MessageId = "m_1",
        Platform = platform,
        ExternalAccountId = "acct",
        From = "sender",
        Timestamp = "1758700800",
        Type = "text",
        TextBody = "Hello",
        ChannelId = "channel",
        ChannelDisplayName = "Channel",
        ReceivedAt = DateTime.UtcNow,
    };

    [Fact]
    public void FacebookMessagesNoLongerThrowAndKeepTheirDiscriminator()
    {
        MessageDocument document = NewFactory().ToDocument(NewMessage(MessagingPlatform.Facebook));

        FacebookMessageDocument facebook = Assert.IsType<FacebookMessageDocument>(document);
        Assert.Equal("channel", facebook.ChannelId);
        Assert.Equal(MessagingPlatform.Facebook, facebook.Platform);
    }

    [Fact]
    public void WhatsAppMessagesStillMapToTheirOwnDocumentType()
    {
        MessageDocument document = NewFactory().ToDocument(NewMessage(MessagingPlatform.WhatsApp));
        Assert.IsType<WhatsAppMessageDocument>(document);
    }

    [Fact]
    public void EveryPlatformMustHaveAMapperOrTheHostFailsToStart()
    {
        // Only WhatsApp registered: construction must fail rather than defer the error to the consumer.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper()]));
        Assert.Contains("Facebook", error.Message);
    }

    [Fact]
    public void AttachmentUrlsSurviveTheMappingInsteadOfBeingDiscarded()
    {
        IncomingMessage message = NewMessage(MessagingPlatform.Facebook) with
        {
            Type = "image",
            TextBody = null,
            AttachmentUrls = ["https://cdn.example.com/photo.jpg"],
        };

        FacebookMessageDocument document = Assert.IsType<FacebookMessageDocument>(NewFactory().ToDocument(message));
        Assert.Equal(["https://cdn.example.com/photo.jpg"], document.AttachmentUrls);
    }

    [Fact]
    public void BothSubtypesRoundTripThroughTheSharedCollectionShape()
    {
        MessageSerializationRegistry.Register();

        foreach (MessagingPlatform platform in Enum.GetValues<MessagingPlatform>())
        {
            MessageDocument original = NewFactory().ToDocument(NewMessage(platform));

            // Serialize as the base type, the way the single collection stores it.
            BsonDocument bson = original.ToBsonDocument<MessageDocument>();
            Assert.True(bson.Contains("_t"), $"{platform} document is missing its discriminator.");
            Assert.Equal("ChannelId", bson.Names.Single(n => n == "ChannelId"));

            MessageDocument roundTripped = BsonSerializer.Deserialize<MessageDocument>(bson);
            Assert.Equal(original.GetType(), roundTripped.GetType());
            Assert.Equal(original.ChannelId, roundTripped.ChannelId);
            Assert.Equal(original.MessageId, roundTripped.MessageId);
        }
    }

    [Fact]
    public void TheStoredFieldNamesAreUnchangedByMovingChannelFieldsToTheBase()
    {
        MessageSerializationRegistry.Register();

        BsonDocument bson = NewFactory().ToDocument(NewMessage(MessagingPlatform.WhatsApp)).ToBsonDocument<MessageDocument>();

        // Existing production documents must keep deserializing, so the wire format is pinned here.
        // ChannelId and ChannelDisplayName moved from the subclass to the base; the BSON is unchanged.
        // Compared as a sorted set: BSON field order carries no meaning, but an added, removed or
        // renamed field would break every document already in the collection.
        Assert.Equal(
            [
                "ChannelDisplayName", "ChannelId", "ConnectionId", "ContactName", "ExternalAccountId",
                "From", "MessageId", "Outgoing", "OwnerUserId", "Platform", "ProviderMessageId",
                "ReceivedAt", "SendStatus", "StoredAt", "TextBody", "Timestamp", "Type", "_id", "_t",
            ],
            bson.Names.Order(StringComparer.Ordinal).ToArray());

        Assert.Equal("whatsapp", bson["_t"].AsString);
        Assert.Equal(1, bson["Platform"].AsInt32);
    }

    [Fact]
    public void DocumentsWrittenByANewerProcessStillDeserialize()
    {
        MessageSerializationRegistry.Register();

        BsonDocument bson = NewFactory().ToDocument(NewMessage(MessagingPlatform.WhatsApp)).ToBsonDocument<MessageDocument>();
        bson.Add("FieldAddedByALaterRelease", "value");

        // BsonIgnoreExtraElements keeps a lagging deploy readable; without it this throws.
        MessageDocument document = BsonSerializer.Deserialize<MessageDocument>(bson);
        Assert.Equal("channel", document.ChannelId);
    }
}
