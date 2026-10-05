using System.Text.Json;
using Parrot.Application.Messaging;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Messaging;

// The bus writes these with MessageJson.Options and the other services read
// them back the same way; a contract that does not survive the round trip, or
// changes shape, breaks every consumer at once.
public class MessageContractSerializationTests
{
    [Fact]
    public void InboundMessage_SurvivesTheBusRoundTrip()
    {
        InboundMessage sent = NewInbound();

        InboundMessage? received = JsonSerializer.Deserialize<InboundMessage>(
            JsonSerializer.Serialize(sent, MessageJson.Options), MessageJson.Options);

        Assert.Equal(sent, received);
    }

    [Fact]
    public void InboundMessage_IsWrittenAsSourceAndData()
    {
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(NewInbound(), MessageJson.Options));
        JsonElement root = json.RootElement;

        Assert.Equal("whatsapp", root.GetProperty("source").GetString());
        // The provider's payload travels as the string it arrived as.
        Assert.Equal(JsonValueKind.String, root.GetProperty("data").ValueKind);
        Assert.Equal("whatsapp:2348012345678:2349011112222", root.GetProperty("conversationKey").GetString());
    }

    [Fact]
    public void InboundMessage_DoesNotInflateThePayloadWithHtmlEscapes()
    {
        string wire = JsonSerializer.Serialize(NewInbound(), MessageJson.Options);

        Assert.DoesNotContain("\\u0022", wire);
        Assert.Contains("\\\"object\\\":\\\"whatsapp_business_account\\\"", wire);
    }

    [Fact]
    public void MessagePersisted_SurvivesTheBusRoundTrip()
    {
        MessagePersisted sent = NewPersisted();

        MessagePersisted? received = JsonSerializer.Deserialize<MessagePersisted>(
            JsonSerializer.Serialize(sent, MessageJson.Options), MessageJson.Options);

        Assert.Equal(sent, received);
    }

    [Fact]
    public void MessagePersisted_IsWrittenAsSourceAndData()
    {
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(NewPersisted(), MessageJson.Options));
        JsonElement root = json.RootElement;

        Assert.Equal("whatsapp", root.GetProperty("source").GetString());
        JsonElement data = root.GetProperty("data");
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Equal("whatsapp:wamid.HBgN", data.GetProperty("messageId").GetString());
        Assert.Equal(42, data.GetProperty("sequence").GetInt64());
        Assert.Equal("user-1", data.GetProperty("ownerUserId").GetString());
    }

    private static InboundMessage NewInbound() => new()
    {
        MessageId = "whatsapp:wamid.HBgN",
        Source = MessagingPlatform.WhatsApp,
        Sender = "2348012345678",
        Receiver = "2349011112222",
        ConversationKey = "whatsapp:2348012345678:2349011112222",
        Data = """{"object":"whatsapp_business_account"}""",
        SentAt = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc),
        ReceivedAt = new DateTime(2026, 10, 1, 9, 30, 1, DateTimeKind.Utc),
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        ConnectionId = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f"),
        OwnerUserId = "user-1",
    };

    private static MessagePersisted NewPersisted() => new()
    {
        Source = MessagingPlatform.WhatsApp,
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new StoredMessage
        {
            MessageId = "whatsapp:wamid.HBgN",
            ConversationKey = "whatsapp:2348012345678:2349011112222",
            Sequence = 42,
            Platform = MessagingPlatform.WhatsApp,
            ConnectionId = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f"),
            OwnerUserId = "user-1",
            Outgoing = false,
            Sender = "2348012345678",
            Receiver = "2349011112222",
            SenderName = "Ada",
            Type = "text",
            Text = "Hello",
            SentAt = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc),
            PersistedAt = new DateTime(2026, 10, 1, 9, 30, 2, DateTimeKind.Utc),
        },
    };
}
