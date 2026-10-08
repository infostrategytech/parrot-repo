using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Infrastructure.Conversations;

namespace Test.Parrot.Api.Tests.Services;

public class WhatsAppSenderTests
{
    [Theory]
    [InlineData(200, "{\"messages\":[{\"id\":\"wamid.reply\"}]}", "accepted")]
    [InlineData(400, "{}", "failed")]
    [InlineData(500, "{}", "unknown")]
    [InlineData(200, "{}", "unknown")]
    public async Task SendsExpectedRequestAndDoesNotRetry(int status, string body, string outcome)
    {
        var handler = new FakeHandler((HttpStatusCode)status, body);
        using var http = new HttpClient(handler);
        var sender = new WhatsAppSender(http, Options.Create(new WhatsAppSettings { AccessToken = "unused-global-token", ApiVersion = "v25.0" }));
        if (outcome == "accepted") Assert.Equal("wamid.reply", await sender.SendAsync("123", "secret", "2348012345678", "Hello", default));
        else if (outcome == "failed") await Assert.ThrowsAsync<WhatsAppRejectedException>(() => sender.SendAsync("123", "secret", "2348012345678", "Hello", default));
        else await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync("123", "secret", "2348012345678", "Hello", default));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://graph.facebook.com/v25.0/123/messages", handler.Url);
        Assert.Equal("Bearer secret", handler.Authorization);
        using var json = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("2348012345678", json.RootElement.GetProperty("to").GetString());
        Assert.Equal("Hello", json.RootElement.GetProperty("text").GetProperty("body").GetString());
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? Payload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Url = request.RequestUri!.ToString(); Authorization = request.Headers.Authorization!.ToString();
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    // Messenger's Send API: the Page's token, the customer's id, a RESPONSE within 24 hours.
    [Theory]
    [InlineData(200, "{\"recipient_id\":\"6215837091842310\",\"message_id\":\"m_reply\"}", "accepted")]
    [InlineData(400, "{\"error\":{\"code\":10,\"error_subcode\":2018278}}", "failed")]
    [InlineData(500, "{}", "unknown")]
    [InlineData(200, "{}", "unknown")]
    public async Task Messenger_SendsExpectedRequestAndDoesNotRetry(int status, string body, string outcome)
    {
        var handler = new FakeHandler((HttpStatusCode)status, body);
        using var http = new HttpClient(handler);
        var sender = new FacebookSender(http, Options.Create(new FacebookSettings { ApiVersion = "v25.0" }));
        if (outcome == "accepted") Assert.Equal("m_reply", await sender.SendAsync("185860297939218", "page-token", "6215837091842310", "Hello", default));
        else if (outcome == "failed")
            Assert.Equal("10", (await Assert.ThrowsAsync<FacebookRejectedException>(() => sender.SendAsync("185860297939218", "page-token", "6215837091842310", "Hello", default))).MetaErrorCode);
        else await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync("185860297939218", "page-token", "6215837091842310", "Hello", default));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://graph.facebook.com/v25.0/185860297939218/messages", handler.Url);
        Assert.Equal("Bearer page-token", handler.Authorization);
        using var json = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("6215837091842310", json.RootElement.GetProperty("recipient").GetProperty("id").GetString());
        Assert.Equal("RESPONSE", json.RootElement.GetProperty("messaging_type").GetString());
        Assert.Equal("Hello", json.RootElement.GetProperty("message").GetProperty("text").GetString());
    }
}
