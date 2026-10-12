using System.Net;
using System.Text;
using Moq;
using Parrot.Web.Features.Dashboard.Channels.Models;
using Parrot.Web.Features.Dashboard.Channels.Services;
using Parrot.Web.Features.Dashboard.Channels.State;

namespace Test.Parrot.Api.Tests.Services.Integrations;

// The Channels screens' view of TikTok: what the card says, what is offered, and what the web calls send.
public class TikTokChannelsTests
{
    private static readonly Guid Connection = Guid.NewGuid();

    private static BusinessChannel Card(ChannelsState state) =>
        state.Workspace.Channels.Single(c => c.Id == ChannelId.TikTok);

    private static TikTokSetup Setup(Guid? connection = null, bool authorized = false, bool lapsed = false) =>
        new(connection, "https://api.example.com/api/tiktok/setup/callback", connection is null ? null : "7123456789",
            authorized, authorized ? "Parrot Test" : null) { NeedsReauthorization = lapsed };

    // ---- the card ------------------------------------------------------------

    [Fact]
    public void TikTokHasACard_AfterFacebook_ThatIsNotConnectedUntilSomethingSaysSo()
    {
        ChannelsState state = new();

        Assert.Equal(["whatsapp", "instagram", "facebook", "tiktok", "twitter"], state.Channels.Select(c => c.Key));
        Assert.Equal(ChannelStatus.NotConnected, Card(state).Status);

        state.SetTikTok(Setup());
        Assert.Equal(ChannelStatus.NotConnected, Card(state).Status);
    }

    // The app is saved on the server, so the screen reopens there: no local "in progress" to start over from.
    [Fact]
    public void ASavedAppWithoutAnAuthorizedAccount_IsNotConnected_AndHasNoLocalSetup()
    {
        ChannelsState state = new();
        state.SetTikTok(Setup(Connection));

        BusinessChannel card = Card(state);

        Assert.Equal(ChannelStatus.NotConnected, card.Status);
        Assert.Null(card.Setup.Mode);
    }

    [Fact]
    public void AnAuthorizedAccount_IsConnected_AndNamedByItsDisplayName()
    {
        ChannelsState state = new();
        state.SetTikTok(Setup(Connection, authorized: true));

        BusinessChannel card = Card(state);

        Assert.Equal(ChannelStatus.Connected, card.Status);
        Assert.Equal("Parrot Test", card.AccountName);
        Assert.Null(card.Attention);
    }

    [Fact]
    public void LapsedAccess_NeedsAttention_WithWhatToDoAboutIt()
    {
        ChannelsState state = new();
        state.SetTikTok(Setup(Connection, authorized: true, lapsed: true));

        BusinessChannel card = Card(state);

        Assert.Equal(ChannelStatus.NeedsAttention, card.Status);
        Assert.Equal(ChannelsState.TikTokReauthorizeAttention, card.Attention);
    }

    [Fact]
    public void Disconnecting_ShowsNotConnected_UntilTheNextSetupIsLoaded()
    {
        ChannelsState state = new();
        state.SetTikTok(Setup(Connection, authorized: true));

        state.DisconnectChannel("tiktok");
        Assert.Equal(ChannelStatus.NotConnected, Card(state).Status);

        state.SetTikTok(Setup(Guid.NewGuid(), authorized: true));
        Assert.Equal(ChannelStatus.Connected, Card(state).Status);
    }

    [Fact]
    public void TheTestMessageNamesTheTikTokAccount_NotAFacebookPage()
    {
        MessageTestCopy copy = ChannelRoutes.Test(ChannelId.TikTok);

        Assert.Contains("TikTok", copy.From + copy.Fallback);
        Assert.DoesNotContain("Facebook", copy.Title + copy.From + copy.Fallback);
    }

    // ---- what is offered -----------------------------------------------------

    [Fact]
    public void TikTokCanBeSetUpByHand_AndDisconnected_ButNotThroughAWindow()
    {
        LiveChannelCapabilities capabilities = new();

        Assert.True(capabilities.CanSetUpManually(ChannelId.TikTok));
        Assert.True(capabilities.CanDisconnect(ChannelId.TikTok));
        Assert.False(capabilities.CanUseWindow(ChannelId.TikTok));
    }

    // ---- the calls the screens make -----------------------------------------

    [Fact]
    public async Task TheSetupIsReadFromTheApi_AsItIsSent()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, $$"""
            {"connectionId":"{{Connection}}","redirectUri":"https://api.example.com/api/tiktok/setup/callback",
             "appId":"7123456789","authorized":true,"displayName":"Parrot Test","needsReauthorization":true}
            """);

        TikTokSetup setup = await Service(handler).GetTikTokSetupAsync("jwt");

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("/api/tiktok/setup", handler.Path);
        Assert.Equal("Bearer jwt", handler.Authorization);
        Assert.Equal(Connection, setup.ConnectionId);
        Assert.True(setup.Authorized);
        Assert.True(setup.NeedsReauthorization);
        Assert.Equal("Parrot Test", setup.DisplayName);
    }

    [Fact]
    public async Task BeforeAnAppIsSaved_TheSetupHasNoConnection()
    {
        RecordingHandler handler = new(HttpStatusCode.OK,
            "{\"connectionId\":null,\"redirectUri\":\"https://api.example.com/api/tiktok/setup/callback\",\"appId\":null,\"authorized\":false,\"displayName\":null,\"needsReauthorization\":false}");

        TikTokSetup setup = await Service(handler).GetTikTokSetupAsync("jwt");

        Assert.Null(setup.ConnectionId);
        Assert.False(setup.Authorized);
    }

    [Fact]
    public async Task SavingTheApp_PutsTheAppIdAndSecret()
    {
        RecordingHandler handler = new(HttpStatusCode.NoContent, "");

        await Service(handler).SaveTikTokAppAsync("7123456789", "app-secret", "jwt");

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("/api/tiktok/setup/app", handler.Path);
        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(handler.Payload!);
        Assert.Equal("7123456789", body.RootElement.GetProperty("appId").GetString());
        Assert.Equal("app-secret", body.RootElement.GetProperty("appSecret").GetString());
    }

    // TikTok's own reason is what tells the owner which of the two they mistyped.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task WhenTheApiRefusesTheApp_ItsReasonIsKept(HttpStatusCode status)
    {
        RecordingHandler handler = new(status,
            "{\"success\":false,\"errorCode\":\"VALIDATION_ERROR\",\"message\":\"TikTok did not accept this App ID and App Secret: App has been blocked or app is not exist.\"}");

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(
            () => Service(handler).SaveTikTokAppAsync("7123456789", "wrong", "jwt"));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal("TikTok did not accept this App ID and App Secret: App has been blocked or app is not exist.", error.Message);
    }

    [Fact]
    public async Task AServerFailure_IsNotShownAsAReason()
    {
        RecordingHandler handler = new(HttpStatusCode.InternalServerError, "{\"message\":\"stack trace details\"}");

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(
            () => Service(handler).SaveTikTokAppAsync("7123456789", "secret", "jwt"));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.DoesNotContain("stack trace", error.Message);
    }

    [Fact]
    public async Task Authorizing_ReadsTheConsentUrl_AndDisconnectingPostsToDisable()
    {
        RecordingHandler authorize = new(HttpStatusCode.OK, "{\"authorizationUrl\":\"https://www.tiktok.com/v2/auth/authorize/?client_key=7123456789\"}");
        TikTokAuthorization authorization = await Service(authorize).GetTikTokAuthorizationAsync("jwt");
        Assert.Equal("/api/tiktok/setup/authorize", authorize.Path);
        Assert.StartsWith("https://www.tiktok.com/v2/auth/authorize/", authorization.AuthorizationUrl);

        RecordingHandler disable = new(HttpStatusCode.NoContent, "");
        await Service(disable).DisableTikTokAsync("jwt");
        Assert.Equal(HttpMethod.Post, disable.Method);
        Assert.Equal("/api/tiktok/setup/disable", disable.Path);
    }

    // ---- the status the other pages read ------------------------------------

    [Fact]
    public async Task TheStatusLoader_ReadsTikTok_AndTreatsAnUnconfiguredServerAsNotConnected()
    {
        Mock<IIntegrationsService> integrations = new();
        integrations.Setup(i => i.GetWhatsAppConnectionsAsync("jwt", default)).ReturnsAsync([]);
        integrations.Setup(i => i.GetFacebookSetupAsync("jwt", default))
            .ReturnsAsync(new FacebookWebhookSetup(Guid.NewGuid(), "u", "t", false, null, null, false));
        integrations.Setup(i => i.GetTikTokSetupAsync("jwt", default)).ReturnsAsync(Setup(Connection, authorized: true));
        ChannelsState state = new();

        Assert.True(await new ChannelStatusLoader(integrations.Object, state).RefreshAsync("jwt", default));
        Assert.Equal(ChannelStatus.Connected, Card(state).Status);

        // TikTok:WebhookBaseUrl is not set on the API: a 503 is "nothing to show", not a failed load.
        integrations.Setup(i => i.GetTikTokSetupAsync("jwt", default))
            .ThrowsAsync(new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable));
        Assert.True(await new ChannelStatusLoader(integrations.Object, new ChannelsState()).RefreshAsync("jwt", default));

        integrations.Setup(i => i.GetTikTokSetupAsync("jwt", default))
            .ThrowsAsync(new HttpRequestException("boom", null, HttpStatusCode.BadGateway));
        Assert.False(await new ChannelStatusLoader(integrations.Object, new ChannelsState()).RefreshAsync("jwt", default));
    }

    private static IntegrationsService Service(RecordingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") });

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }
        public string? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            Payload = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
