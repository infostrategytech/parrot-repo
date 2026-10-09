using System.Text.Json;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Enums;
using Test.Parrot.Api.Tests.Services.Integrations;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class TikTokDecodingTests
{
    private static readonly InboundMessageDecoders Decoders = new([new TikTokInboundDecoder()]);

    [Fact]
    public void ATextMessage_DecodesToTheSharedShape()
    {
        IncomingMessage message = Decoders.Decode(Inbound("tiktok:m1", TikTokWebhookTests.Event("im_receive_msg", Content())));

        Assert.Equal(MessagingPlatform.TikTok, message.Platform);
        Assert.Equal("tiktok:m1", message.MessageId);
        Assert.Equal("biz-open-id", message.ExternalAccountId);
        Assert.Equal("biz-open-id", message.ChannelId);
        Assert.Equal("customer-open-id", message.From);
        Assert.Equal("Ada", message.ContactName);
        Assert.Equal("text", message.Type);
        Assert.Equal("Hello", message.TextBody);
        Assert.Equal("conversation-1", message.ExternalConversationId);
        Assert.Equal("1700000000123", message.Timestamp);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, 123, DateTimeKind.Utc), message.SentAt);
        Assert.Null(message.AttachmentUrls);
        Assert.Equal(Guid.Empty, message.ConnectionId);
    }

    [Fact]
    public void ASharedPost_KeepsItsLinkAndType()
    {
        object content = Content(type: "share_post", extra: new { share_post = new { embed_url = "https://www.tiktok.com/embed/1" } });

        IncomingMessage message = Decoders.Decode(Inbound("tiktok:m1", TikTokWebhookTests.Event("im_receive_msg", content)));

        Assert.Equal("share_post", message.Type);
        Assert.Equal(["https://www.tiktok.com/embed/1"], message.AttachmentUrls);
    }

    // An image arrives as a media id that needs a second call to download, so only its type is kept for now.
    [Fact]
    public void AnImage_IsStoredByTypeWithoutText()
    {
        object content = Content(type: "IMAGE", text: null, extra: new { image = new { media_id = "media-1" } });

        IncomingMessage message = Decoders.Decode(Inbound("tiktok:m1", TikTokWebhookTests.Event("im_receive_msg", content)));

        Assert.Equal("image", message.Type);
        Assert.Null(message.TextBody);
    }

    [Fact]
    public void ContentSentAsAnObject_ReadsTheSameAsContentSentAsText()
    {
        string asObject = JsonSerializer.Serialize(new
        {
            @event = "im_receive_msg", user_openid = "biz-open-id", content = Content(),
        });

        Assert.Equal("Hello", Decoders.Decode(Inbound("tiktok:m1", asObject)).TextBody);
    }

    [Fact]
    public void AMessageWithoutATimestamp_FallsBackToWhenTheWebhookSentIt()
    {
        InboundMessage inbound = Inbound("tiktok:m1", TikTokWebhookTests.Event("im_receive_msg", Content(withTimestamp: false))) with
        {
            SentAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        IncomingMessage message = Decoders.Decode(inbound);

        Assert.Equal(inbound.SentAt, message.SentAt);
        Assert.Equal(string.Empty, message.Timestamp);
    }

    [Theory]
    [InlineData("whatsapp:m1", "message_id_not_prefixed")]
    [InlineData("tiktok:other", "message_not_in_body")]
    public void AnIdThatIsNotInTheBody_IsUnprocessable(string messageId, string reason)
    {
        UnprocessableMessageException error = Assert.Throws<UnprocessableMessageException>(
            () => Decoders.Decode(Inbound(messageId, TikTokWebhookTests.Event("im_receive_msg", Content()))));

        Assert.Equal(reason, error.Message);
    }

    [Fact]
    public void AnEchoOrAnUnreadableBody_IsUnprocessable()
    {
        Assert.Throws<UnprocessableMessageException>(
            () => Decoders.Decode(Inbound("tiktok:m1", TikTokWebhookTests.Event("im_send_msg", Content()))));
        Assert.Equal("body_not_json", Assert.Throws<UnprocessableMessageException>(
            () => Decoders.Decode(Inbound("tiktok:m1", "not json"))).Message);
        Assert.Equal("empty_body", Assert.Throws<UnprocessableMessageException>(
            () => Decoders.Decode(Inbound("tiktok:m1", " "))).Message);
    }

    private static object Content(string type = "text", string? text = "Hello", bool withTimestamp = true, object? extra = null)
    {
        Dictionary<string, object?> content = new()
        {
            ["message_id"] = "m1",
            ["conversation_id"] = "conversation-1",
            ["type"] = type,
            ["from"] = "Ada",
            ["from_user"] = new { id = "customer-open-id" },
            ["to_user"] = new { id = "biz-open-id" },
        };
        if (withTimestamp) content["timestamp"] = 1700000000123;
        if (text is not null) content["text"] = new { body = text };
        if (extra is not null)
            foreach (System.Reflection.PropertyInfo property in extra.GetType().GetProperties())
                content[property.Name] = property.GetValue(extra);
        return content;
    }

    private static InboundMessage Inbound(string messageId, string data) => new()
    {
        MessageId = messageId, Source = MessagingPlatform.TikTok, Sender = "customer-open-id", Receiver = "biz-open-id",
        ConversationKey = "tiktok:biz-open-id:customer-open-id", Data = data, ReceivedAt = DateTime.UtcNow,
        CorrelationId = "487b7279745d4c41675818ffee460c54", ConnectionId = Guid.Empty,
    };
}
