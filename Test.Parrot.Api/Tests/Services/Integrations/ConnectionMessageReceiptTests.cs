using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class ConnectionMessageReceiptTests
{
    [Fact]
    public void MongoDocumentPreservesConnectionOwnership()
    {
        Guid id = Guid.NewGuid();
        WhatsAppMessageDocument original = new()
        {
            ConnectionId = id, OwnerUserId = "owner", MessageId = "message",
            Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "business",
            From = "sender", Timestamp = "123", Type = "text", ChannelId = "phone", ChannelDisplayName = "number",
        };
        WhatsAppMessageDocument restored = BsonSerializer.Deserialize<WhatsAppMessageDocument>(original.ToBson());
        Assert.Equal(id, restored.ConnectionId);
        Assert.Equal("owner", restored.OwnerUserId);
    }
}
