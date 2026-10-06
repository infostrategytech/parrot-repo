using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class InboundMessageDecodingTests
{
    private static readonly InboundMessageDecoders Decoders = new([new WhatsAppInboundDecoder(), new FacebookInboundDecoder()]);
    private static readonly Guid Connection = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f");

    // One Meta webhook carrying two messages from two customers.
    private const string WhatsAppBody = """
        {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp",
          "metadata":{"display_phone_number":"+234 901 111 2222","phone_number_id":"phone-1"},
          "contacts":[{"wa_id":"2348012345678","profile":{"name":"Ada"}},{"wa_id":"2348033334444","profile":{"name":"Tunde"}}],
          "messages":[
            {"from":"2348012345678","id":"wamid.A","timestamp":"1790589123","type":"text","text":{"body":"Hello"}},
            {"from":"2348033334444","id":"wamid.B","timestamp":"1790589124","type":"image"}
          ]}}]}]}
        """;

    private const string FacebookBody = """
        {"object":"page","entry":[{"id":"page-1","time":1790589123000,"messaging":[
          {"sender":{"id":"psid-1"},"recipient":{"id":"page-1"},"timestamp":1790589123000,
           "message":{"mid":"m.echo","text":"from the page","is_echo":true}},
          {"sender":{"id":"psid-1"},"recipient":{"id":"page-1"},"timestamp":1790589123500,
           "message":{"mid":"m.1","attachments":[{"type":"image","payload":{"url":"https://cdn.example/a.jpg"}}]}},
          {"sender":{"id":"psid-2"},"recipient":{"id":"page-1"},"timestamp":1790589124000,
           "postback":{"mid":"m.2","title":"Start","payload":"GET_STARTED"}}
        ]}]}
        """;

    // Meta leaves out a contact's profile, and a username customer's wa_id;
    // a status to such a customer has no recipient_id.
    private const string WhatsAppBodyWithBareContacts = """
        {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp",
          "metadata":{"display_phone_number":"+234 901 111 2222","phone_number_id":"phone-1"},
          "contacts":[{"wa_id":"2348012345678"},{"profile":{"name":"Tunde"}},{"wa_id":"2348055556666","profile":{}}],
          "messages":[{"from":"2348012345678","id":"wamid.C","timestamp":"1790589125","type":"text","text":{"body":"Hi"}}],
          "statuses":[{"id":"wamid.D","status":"delivered","timestamp":"1790589126"}]
          }}]}]}
        """;

    private static InboundMessage Inbound(MessagingPlatform source, string messageId, string data) => new()
    {
        MessageId = messageId,
        Source = source,
        Sender = "2348012345678",
        Receiver = "2349011112222",
        ConversationKey = "whatsapp:2348012345678:2349011112222",
        Data = data,
        SentAt = new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc),
        ReceivedAt = new DateTime(2026, 10, 2, 9, 30, 1, DateTimeKind.Utc),
        CorrelationId = "487b7279745d4c41675818ffee460c54",
        ConnectionId = Connection,
        OwnerUserId = "user-1",
    };

    [Fact]
    public void WhatsApp_DecodesTheMessageThisInboundIsAbout()
    {
        IncomingMessage message = Decoders.Decode(Inbound(MessagingPlatform.WhatsApp, "whatsapp:wamid.A", WhatsAppBody));

        Assert.Equal("whatsapp:wamid.A", message.MessageId);
        Assert.Equal(MessagingPlatform.WhatsApp, message.Platform);
        Assert.Equal("2348012345678", message.From);
        Assert.Equal("text", message.Type);
        Assert.Equal("Hello", message.TextBody);
        Assert.Equal("Ada", message.ContactName);
        Assert.Equal("waba-1", message.ExternalAccountId);
        Assert.Equal("phone-1", message.ChannelId);
        Assert.Equal("+234 901 111 2222", message.ChannelDisplayName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790589123).UtcDateTime, message.SentAt);
        Assert.Equal(Connection, message.ConnectionId);
        Assert.Equal("user-1", message.OwnerUserId);
        Assert.Equal(new DateTime(2026, 10, 2, 9, 30, 1, DateTimeKind.Utc), message.ReceivedAt);
    }

    // The same body arrives once per message it holds; each picks its own.
    [Fact]
    public void WhatsApp_PicksTheOtherMessageFromTheSameBody()
    {
        IncomingMessage message = Decoders.Decode(Inbound(MessagingPlatform.WhatsApp, "whatsapp:wamid.B", WhatsAppBody));

        Assert.Equal("2348033334444", message.From);
        Assert.Equal("image", message.Type);
        Assert.Null(message.TextBody);
        Assert.Equal("Tunde", message.ContactName);
    }

    [Fact]
    public void Facebook_DecodesAMessageWithItsAttachments()
    {
        IncomingMessage message = Decoders.Decode(Inbound(MessagingPlatform.Facebook, "facebook:m.1", FacebookBody));

        Assert.Equal("facebook:m.1", message.MessageId);
        Assert.Equal("psid-1", message.From);
        Assert.Equal("image", message.Type);
        Assert.Equal(["https://cdn.example/a.jpg"], message.AttachmentUrls);
        Assert.Equal("page-1", message.ChannelId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790589123500).UtcDateTime, message.SentAt);
    }

    [Fact]
    public void Facebook_DecodesAPostback()
    {
        IncomingMessage message = Decoders.Decode(Inbound(MessagingPlatform.Facebook, "facebook:m.2", FacebookBody));

        Assert.Equal("postback", message.Type);
        Assert.Equal("GET_STARTED", message.TextBody);
    }

    // Echoes are the Page's own sends; the webhook never publishes them, and the decoder never matches one.
    [Fact]
    public void Facebook_NeverDecodesAnEcho()
    {
        UnprocessableMessageException ex = Assert.Throws<UnprocessableMessageException>(
            () => Decoders.Decode(Inbound(MessagingPlatform.Facebook, "facebook:m.echo", FacebookBody)));
        Assert.Equal("message_not_in_body", ex.Reason);
    }

    [Theory]
    [InlineData("whatsapp:wamid.MISSING", WhatsAppBody, "message_not_in_body")]
    [InlineData("whatsapp:wamid.A", "not json", "body_not_json")]
    [InlineData("whatsapp:wamid.A", "", "empty_body")]
    [InlineData("wamid.A", WhatsAppBody, "message_id_not_prefixed")]
    [InlineData("whatsapp:wamid.A", """{"object":"whatsapp_business_account"}""", "body_not_json")]
    public void WhatsApp_WhatCanNeverDecodeIsUnprocessable(string messageId, string body, string reason)
    {
        UnprocessableMessageException ex = Assert.Throws<UnprocessableMessageException>(
            () => Decoders.Decode(Inbound(MessagingPlatform.WhatsApp, messageId, body)));

        Assert.Equal(reason, ex.Reason);
    }

    // The reason goes into dead-letter headers and logs, so it must never carry the body.
    [Fact]
    public void TheReasonNeverContainsMessageContent()
    {
        UnprocessableMessageException ex = Assert.Throws<UnprocessableMessageException>(
            () => Decoders.Decode(Inbound(MessagingPlatform.WhatsApp, "whatsapp:wamid.A", "{\"Ada\": \"Hello 2348012345678\"")));

        Assert.DoesNotContain("Ada", ex.Reason);
        Assert.DoesNotContain("2348012345678", ex.Reason);
    }

    [Fact]
    public void AnUnknownSourceIsUnprocessable()
    {
        InboundMessageDecoders whatsAppOnly = new([new WhatsAppInboundDecoder()]);

        Assert.Throws<UnprocessableMessageException>(
            () => whatsAppOnly.Decode(Inbound(MessagingPlatform.Facebook, "facebook:m.1", FacebookBody)));
    }

    // The API binds the webhook with the web JSON defaults; a required field
    // Meta left out failed the whole batch with a 400 and lost its messages.
    [Fact]
    public void WhatsApp_APayloadWithBareContacts_StillBinds()
    {
        WhatsAppWebhookPayload? payload = System.Text.Json.JsonSerializer.Deserialize<WhatsAppWebhookPayload>(
            WhatsAppBodyWithBareContacts, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        WhatsAppChangeValue value = payload!.Entry[0].Changes[0].Value;
        Assert.Equal(3, value.Contacts!.Count);
        Assert.Null(value.Contacts[0].Profile);
        Assert.Null(value.Contacts[1].WaId);
        Assert.Null(value.Contacts[2].Profile!.Name);
        Assert.Null(value.Statuses![0].RecipientId);
    }

    [Fact]
    public void WhatsApp_AContactWithNoProfile_DecodesWithNoName()
    {
        IncomingMessage message = Decoders.Decode(Inbound(MessagingPlatform.WhatsApp, "whatsapp:wamid.C", WhatsAppBodyWithBareContacts));

        Assert.Equal("2348012345678", message.From);
        Assert.Equal("Hi", message.TextBody);
        Assert.Null(message.ContactName);
    }
}
