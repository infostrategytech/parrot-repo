using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Infrastructure.Conversations;

namespace Test.Parrot.Api.Tests.Services;

// Replying to a message, as each channel's Send API takes it: WhatsApp's context.message_id, Messenger's and
// Instagram's top-level reply_to.mid. A plain reply carries neither.
public class ReplySendTests
{
    [Fact]
    public async Task WhatsApp_AReplyCarriesTheQuotedMessagesWamid()
    {
        Handler handler = new((HttpStatusCode.OK, """{"messages":[{"id":"wamid.reply"}]}"""));
        WhatsAppSender sender = new(new HttpClient(handler), Options.Create(new WhatsAppSettings { ApiVersion = "v25.0" }));

        Assert.Equal("wamid.reply", await sender.SendReplyAsync("123", "secret", "2348012345678", "Yes, we do", "wamid.question", default));

        JsonElement sent = handler.Payloads.Single();
        Assert.Equal("wamid.question", sent.GetProperty("context").GetProperty("message_id").GetString());
        Assert.Equal("Yes, we do", sent.GetProperty("text").GetProperty("body").GetString());
    }

    [Fact]
    public async Task WhatsApp_APlainReplyHasNoContext()
    {
        Handler handler = new((HttpStatusCode.OK, """{"messages":[{"id":"wamid.reply"}]}"""));
        WhatsAppSender sender = new(new HttpClient(handler), Options.Create(new WhatsAppSettings { ApiVersion = "v25.0" }));

        await sender.SendAsync("123", "secret", "2348012345678", "Hello", default);

        Assert.False(handler.Payloads.Single().TryGetProperty("context", out _));
    }

    [Fact]
    public async Task Messenger_AReplyCarriesReplyToAtTheTop()
    {
        Handler handler = new((HttpStatusCode.OK, """{"recipient_id":"1","message_id":"m_reply"}"""));
        FacebookSender sender = new(new HttpClient(handler), Options.Create(new FacebookSettings { ApiVersion = "v25.0" }));

        Assert.Equal("m_reply", await sender.SendReplyAsync("page", "token", "psid", "A little", "m_question", default));

        JsonElement sent = handler.Payloads.Single();
        Assert.Equal("m_question", sent.GetProperty("reply_to").GetProperty("mid").GetString());
        Assert.Equal("RESPONSE", sent.GetProperty("messaging_type").GetString());
        Assert.Equal("A little", sent.GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Instagram_AReplyCarriesReplyTo()
    {
        Handler handler = new((HttpStatusCode.OK, """{"recipient_id":"1","message_id":"ig_reply"}"""));
        InstagramSender sender = new(new HttpClient(handler), Options.Create(new InstagramSettings { ApiVersion = "v25.0" }));

        Assert.Equal("ig_reply", await sender.SendReplyAsync("acct", "token", "igsid", "Yes", "ig_question", default));

        Assert.Equal("ig_question", handler.Payloads.Single().GetProperty("reply_to").GetProperty("mid").GetString());
    }

    // Refused outright, the first send never reached the customer: sending the text without the quote cannot double it.
    [Fact]
    public async Task Instagram_AnAccountThatRefusesTheQuote_StillGetsTheReply()
    {
        Handler handler = new(
            (HttpStatusCode.BadRequest, """{"error":{"code":100,"message":"Invalid parameter"}}"""),
            (HttpStatusCode.OK, """{"recipient_id":"1","message_id":"ig_plain"}"""));
        InstagramSender sender = new(new HttpClient(handler), Options.Create(new InstagramSettings { ApiVersion = "v25.0" }));

        Assert.Equal("ig_plain", await sender.SendReplyAsync("acct", "token", "igsid", "Yes", "ig_question", default));

        Assert.Equal(2, handler.Payloads.Count);
        Assert.False(handler.Payloads[1].TryGetProperty("reply_to", out _));
        Assert.Equal("Yes", handler.Payloads[1].GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Instagram_AnyOtherRefusal_IsNotRetried()
    {
        Handler handler = new((HttpStatusCode.BadRequest, """{"error":{"code":10}}"""));
        InstagramSender sender = new(new HttpClient(handler), Options.Create(new InstagramSettings { ApiVersion = "v25.0" }));

        await Assert.ThrowsAsync<InstagramRejectedException>(() => sender.SendReplyAsync("acct", "token", "igsid", "Yes", "ig_question", default));
        Assert.Single(handler.Payloads);
    }

    private sealed class Handler(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        public List<JsonElement> Payloads { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using JsonDocument json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Payloads.Add(json.RootElement.Clone());
            (HttpStatusCode status, string body) = answers[Math.Min(Payloads.Count, answers.Length) - 1];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
