using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Infrastructure.SocialMediaIntegrations;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class TikTokApiClientTests
{
    private const string Ok = "{\"code\":0,\"message\":\"OK\",\"data\":{}}";

    [Fact]
    public async Task RegisteringTheWebhook_PostsTheAppAndCallbackForDirectMessages()
    {
        FakeHandler handler = new(HttpStatusCode.OK, Ok);

        await Client(handler).RegisterWebhookAsync("7123456789", "app-secret", "https://api.example.com/api/webhooks/tiktok/abc", default);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://business-api.tiktok.com/open_api/v1.3/business/webhook/update/", handler.Url);
        using JsonDocument body = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("7123456789", body.RootElement.GetProperty("app_id").GetString());
        Assert.Equal("app-secret", body.RootElement.GetProperty("secret").GetString());
        Assert.Equal("DIRECT_MESSAGE", body.RootElement.GetProperty("event_type").GetString());
        Assert.Equal("https://api.example.com/api/webhooks/tiktok/abc", body.RootElement.GetProperty("callback_url").GetString());
    }

    [Fact]
    public async Task ExchangingTheCode_ReturnsTheTokensAndTheirLifetimes()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """
            {"code":0,"message":"OK","data":{"open_id":"business-open-id","scope":"user.info.basic, message.list.send",
             "access_token":"access-token","expires_in":86400,"refresh_token":"refresh-token","refresh_token_expires_in":"2592000"}}
            """);

        TikTokTokens tokens = await Client(handler).ExchangeCodeAsync("7123456789", "app-secret", "auth-code",
            "https://api.example.com/api/tiktok/setup/callback", default);

        Assert.Equal("https://business-api.tiktok.com/open_api/v1.3/tt_user/oauth2/token/", handler.Url);
        using JsonDocument body = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("7123456789", body.RootElement.GetProperty("client_id").GetString());
        Assert.Equal("app-secret", body.RootElement.GetProperty("client_secret").GetString());
        Assert.Equal("authorization_code", body.RootElement.GetProperty("grant_type").GetString());
        Assert.Equal("auth-code", body.RootElement.GetProperty("auth_code").GetString());
        Assert.Equal("https://api.example.com/api/tiktok/setup/callback", body.RootElement.GetProperty("redirect_uri").GetString());

        Assert.Equal("business-open-id", tokens.OpenId);
        Assert.Equal(new[] { "user.info.basic", "message.list.send" }, tokens.Scopes);
        Assert.Equal("access-token", tokens.AccessToken);
        Assert.Equal(TimeSpan.FromDays(1), tokens.AccessTokenLifetime);
        Assert.Equal("refresh-token", tokens.RefreshToken);
        Assert.Equal(TimeSpan.FromDays(30), tokens.RefreshTokenLifetime);
    }

    [Fact]
    public async Task ReadingTheAccount_SendsTheAccessTokenHeaderAndFieldList()
    {
        FakeHandler handler = new(HttpStatusCode.OK,
            "{\"code\":0,\"message\":\"OK\",\"data\":{\"username\":\"parrot_test\",\"display_name\":\"Parrot Test\"}}");

        TikTokAccount account = await Client(handler).GetAccountAsync("business-open-id", "access-token", default);

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("https://business-api.tiktok.com/open_api/v1.3/business/get/"
            + "?business_id=business-open-id&fields=%5B%22username%22%2C%22display_name%22%5D", handler.Url);
        Assert.Equal("access-token", handler.AccessToken);
        Assert.Equal(new TikTokAccount("parrot_test", "Parrot Test"), account);
    }

    // TikTok reports most refusals in the body of a 200.
    [Fact]
    public async Task ANonZeroCode_IsARefusalWithTikToksMessage()
    {
        FakeHandler handler = new(HttpStatusCode.OK, "{\"code\":40001,\"message\":\"Invalid app_id or secret.\",\"data\":{}}");

        TikTokApiException error = await Assert.ThrowsAsync<TikTokApiException>(
            () => Client(handler).RegisterWebhookAsync("7123456789", "wrong", "https://api.example.com/x", default));

        Assert.Equal(40001, error.Code);
        Assert.Equal("Invalid app_id or secret.", error.Message);
    }

    [Fact]
    public async Task AClientErrorWithoutJson_IsStillARefusal()
    {
        FakeHandler handler = new(HttpStatusCode.Forbidden, "<html>Forbidden</html>");

        TikTokApiException error = await Assert.ThrowsAsync<TikTokApiException>(
            () => Client(handler).RegisterWebhookAsync("7123456789", "secret", "https://api.example.com/x", default));

        Assert.Equal(403, error.Code);
    }

    [Fact]
    public async Task AServerError_IsTransient_NotARefusal()
    {
        FakeHandler handler = new(HttpStatusCode.BadGateway, Ok);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(handler).RegisterWebhookAsync("7123456789", "secret", "https://api.example.com/x", default));
    }

    [Fact]
    public async Task ATokenResponseWithoutAnAccessToken_IsRejected()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """
            {"code":0,"message":"OK","data":{"open_id":"business-open-id","expires_in":86400,
             "refresh_token":"refresh-token","refresh_token_expires_in":2592000}}
            """);

        await Assert.ThrowsAsync<JsonException>(() => Client(handler).ExchangeCodeAsync("7123456789", "secret", "code",
            "https://api.example.com/api/tiktok/setup/callback", default));
    }

    private static TikTokApiClient Client(FakeHandler handler) =>
        new(new HttpClient(handler), Options.Create(new TikTokSettings()));

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? AccessToken { get; private set; }
        public string? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Url = request.RequestUri!.AbsoluteUri;
            AccessToken = request.Headers.TryGetValues("Access-Token", out IEnumerable<string>? values) ? values.Single() : null;
            Payload = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
