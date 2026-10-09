using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Rest.Controllers;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.SocialMediaIntegrations;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class TikTokSetupTests
{
    private const string Owner = "owner";
    private const string RedirectUri = "https://api.example.com/api/tiktok/setup/callback";

    private readonly MemoryTikTokRepository _repository = new();
    private readonly FakeTikTok _tikTok = new();
    private readonly TikTokCredentialProtector _protector = new(new EphemeralDataProtectionProvider());

    [Fact]
    public async Task SetupReturnsTheOwnersStep_AndRejectsAMissingOwner()
    {
        Mock<ITikTokConnectionService> connections = new();
        TikTokSetupResponse expected = new(null, RedirectUri, null, false, null);
        connections.Setup(x => x.GetSetupAsync(Owner, default)).ReturnsAsync(expected);

        OkObjectResult response = Assert.IsType<OkObjectResult>(await Controller(connections.Object, Owner).Get(default));
        Assert.Same(expected, response.Value);
        Assert.IsType<UnauthorizedResult>(await Controller(connections.Object, owner: null).Get(default));
    }

    [Fact]
    public async Task BeforeAnythingIsSaved_SetupShowsOnlyTheRedirectUri()
    {
        TikTokSetupResponse setup = await NewService().GetSetupAsync(Owner);

        Assert.Equal(new TikTokSetupResponse(null, RedirectUri, null, false, null), setup);
    }

    [Fact]
    public async Task SavingTheAppRegistersItsOwnWebhook_AndStoresTheSecretEncrypted()
    {
        await NewService().SaveAppAsync(Owner, App(" 7123456789 ", " app-secret "));

        TikTokConnection connection = _repository.Connection!;
        Assert.Equal(("7123456789", "app-secret", $"https://api.example.com/api/webhooks/tiktok/{connection.WebhookIdentifier}"),
            _tikTok.RegisteredWebhook);
        Assert.Equal("7123456789", connection.AppId);
        Assert.NotEqual("app-secret", connection.AppSecretEncrypted);
        Assert.Equal("app-secret", _protector.Unprotect(connection.AppSecretEncrypted));

        TikTokSetupResponse setup = await NewService().GetSetupAsync(Owner);
        Assert.Equal(connection.Id, setup.ConnectionId);
        Assert.False(setup.Authorized);
        Assert.DoesNotContain("app-secret", System.Text.Json.JsonSerializer.Serialize(setup));
    }

    [Fact]
    public async Task AnAppTikTokRefuses_IsReportedAndLeavesNoConnection()
    {
        _tikTok.Failure = new TikTokApiException(40001, "Invalid app_id or secret.");

        ValidationException error = await Assert.ThrowsAsync<ValidationException>(
            () => NewService().SaveAppAsync(Owner, App("7123456789", "wrong")));

        Assert.Contains("Invalid app_id or secret.", error.Message);
        Assert.Null(_repository.Connection);
    }

    [Fact]
    public async Task WhenTikTokCannotBeReached_TheCallerCanRetry_AndNothingIsSaved()
    {
        _tikTok.Failure = new HttpRequestException("timeout");

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => NewService().SaveAppAsync(Owner, App("7123456789", "secret")));
        Assert.Null(_repository.Connection);
    }

    [Theory]
    [InlineData("71234 56789", "secret")]
    [InlineData("7123456789", "sec\tret")]
    [InlineData("7123456789", "   ")]
    public async Task ACredentialThatIsNotOneToken_IsRejectedBeforeCallingTikTok(string appId, string appSecret)
    {
        await Assert.ThrowsAsync<ValidationException>(() => NewService().SaveAppAsync(Owner, App(appId, appSecret)));

        Assert.Null(_tikTok.RegisteredWebhook);
        Assert.Null(_repository.Connection);
    }

    [Fact]
    public async Task ASecondApp_IsRefusedUntilTheFirstIsDisconnected()
    {
        TikTokConnectionService service = NewService();
        await service.SaveAppAsync(Owner, App("7123456789", "secret"));

        await Assert.ThrowsAsync<ConflictException>(() => service.SaveAppAsync(Owner, App("7000000000", "other")));

        await service.DisableAsync(Owner);
        await service.SaveAppAsync(Owner, App("7000000000", "other"));
        Assert.Equal("7000000000", _repository.Connection!.AppId);
    }

    [Fact]
    public async Task TheConsentUrlIsForTheOwnersApp_WithTheScopesAndRedirectUri()
    {
        TikTokConnectionService service = NewService();
        await service.SaveAppAsync(Owner, App("7123456789", "secret"));

        Uri url = new((await service.GetAuthorizationAsync(Owner)).AuthorizationUrl);
        Dictionary<string, string> query = QueryHelpers.ParseQuery(url.Query).ToDictionary(p => p.Key, p => p.Value.ToString());

        Assert.Equal("https://www.tiktok.com/v2/auth/authorize/", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("7123456789", query["client_key"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(RedirectUri, query["redirect_uri"]);
        Assert.Equal(string.Join(',', TikTokConnectionService.Scopes), query["scope"]);
        Assert.False(string.IsNullOrEmpty(query["state"]));
    }

    [Fact]
    public async Task ConsentBeforeTheAppIsSaved_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => NewService().GetAuthorizationAsync(Owner));
    }

    [Fact]
    public async Task CompletingTheConsent_SavesTheAccountAndEncryptedTokensWithTheirExpiry()
    {
        TikTokConnectionService service = NewService();
        await service.SaveAppAsync(Owner, App("7123456789", "secret"));
        string state = await StateAsync(service);

        DateTime before = DateTime.UtcNow;
        await service.CompleteAuthorizationAsync(state, "auth-code");

        Assert.Equal(("7123456789", "secret", "auth-code", RedirectUri), _tikTok.Exchanged);
        Assert.Equal(("business-open-id", "access-token"), _tikTok.AccountRequested);
        TikTokConnection connection = _repository.Connection!;
        Assert.Equal("business-open-id", connection.TikTokOpenId);
        Assert.Equal("Parrot Test", connection.DisplayName);
        Assert.Equal("access-token", _protector.Unprotect(connection.AccessTokenEncrypted!));
        Assert.Equal("refresh-token", _protector.Unprotect(connection.RefreshTokenEncrypted!));
        Assert.InRange(connection.AccessTokenExpiresAt!.Value, before.AddHours(24), DateTime.UtcNow.AddHours(24));
        Assert.InRange(connection.RefreshTokenExpiresAt!.Value, before.AddDays(30), DateTime.UtcNow.AddDays(30));

        TikTokSetupResponse setup = await service.GetSetupAsync(Owner);
        Assert.True(setup.Authorized);
        Assert.Equal("Parrot Test", setup.DisplayName);
    }

    // Access that cannot be renewed any more: the setup says so, so the screen can ask for the consent again.
    [Fact]
    public async Task WhenTheRefreshTokenHasLapsed_TheSetupAsksToAuthorizeAgain()
    {
        TikTokConnectionService service = NewService();
        await service.SaveAppAsync(Owner, App("7123456789", "secret"));
        await service.CompleteAuthorizationAsync(await StateAsync(service), "auth-code");
        Assert.False((await service.GetSetupAsync(Owner)).NeedsReauthorization);

        _repository.Connection!.RefreshTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);

        TikTokSetupResponse setup = await service.GetSetupAsync(Owner);
        Assert.True(setup.Authorized);
        Assert.True(setup.NeedsReauthorization);
    }

    [Fact]
    public async Task AnAccountWithoutADisplayName_IsNamedByItsUsername()
    {
        _tikTok.Account = new TikTokAccount("parrot_test", " ");
        TikTokConnectionService service = NewService();
        await service.SaveAppAsync(Owner, App("7123456789", "secret"));

        await service.CompleteAuthorizationAsync(await StateAsync(service), "auth-code");

        Assert.Equal("parrot_test", _repository.Connection!.DisplayName);
    }

    [Fact]
    public async Task PermissionsTheOwnerUnticked_AreNamed_AndNothingIsSaved()
    {
        _tikTok.Tokens = _tikTok.Tokens with { Scopes = ["user.info.basic", "message.list.read"] };
        TikTokConnectionService service = NewService();
        await service.SaveAppAsync(Owner, App("7123456789", "secret"));

        ValidationException error = await Assert.ThrowsAsync<ValidationException>(
            async () => await service.CompleteAuthorizationAsync(await StateAsync(service), "auth-code"));

        Assert.Contains("message.list.send", error.Message);
        Assert.Null(_repository.Connection!.TikTokOpenId);
        Assert.Null(_repository.Connection.AccessTokenEncrypted);
    }

    [Fact]
    public async Task AnAlteredOrExpiredState_IsRefusedWithoutCallingTikTok()
    {
        TikTokConnectionService service = NewService();
        await service.SaveAppAsync(Owner, App("7123456789", "secret"));
        string state = await StateAsync(service);
        string altered = state[..10] + (state[10] == 'A' ? 'B' : 'A') + state[11..];
        string expired = _protector.ProtectState($"{_repository.Connection!.Id:N}{Owner}", TimeSpan.FromSeconds(-1));

        await Assert.ThrowsAsync<ValidationException>(() => service.CompleteAuthorizationAsync(altered, "auth-code"));
        await Assert.ThrowsAsync<ValidationException>(() => service.CompleteAuthorizationAsync(expired, "auth-code"));
        await Assert.ThrowsAsync<ValidationException>(() => service.CompleteAuthorizationAsync(state, code: null));

        Assert.Null(_tikTok.Exchanged);
    }

    // A consent started before a disconnect must not attach the account to the new setup.
    [Fact]
    public async Task AStateFromADisconnectedSetup_IsRefused()
    {
        TikTokConnectionService service = NewService();
        await service.SaveAppAsync(Owner, App("7123456789", "secret"));
        string oldState = await StateAsync(service);
        await service.DisableAsync(Owner);
        await service.SaveAppAsync(Owner, App("7123456789", "secret"));

        await Assert.ThrowsAsync<ConflictException>(() => service.CompleteAuthorizationAsync(oldState, "auth-code"));
        Assert.Null(_tikTok.Exchanged);
    }

    [Fact]
    public async Task DisconnectWithNothingConnected_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => NewService().DisableAsync(Owner));
    }

    [Fact]
    public async Task SetupFailsBeforeCallingTikTokWhenTheBaseUrlIsNotConfigured()
    {
        TikTokConnectionService service = new(_repository, _protector, _tikTok,
            Options.Create(new TikTokSettings { WebhookBaseUrl = "http://api.example.com" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAppAsync(Owner, App("7123456789", "secret")));
        Assert.Null(_tikTok.RegisteredWebhook);
        Assert.Null(_repository.Connection);
    }

    [Fact]
    public async Task TheCallbackEndsOnTheChannelsPage_WithTheOutcome()
    {
        Mock<ITikTokConnectionService> connections = new();
        connections.Setup(x => x.CompleteAuthorizationAsync("bad", "code", default))
            .ThrowsAsync(new ValidationException("This TikTok authorization has expired. Start again."));
        connections.Setup(x => x.CompleteAuthorizationAsync("broken", "code", default))
            .ThrowsAsync(new InvalidOperationException("secret detail"));
        TikTokSetupController controller = Controller(connections.Object, owner: null);
        const string Channels = "https://app.example.com/dashboard/channels";

        Assert.Equal($"{Channels}?tiktok=connected",
            Assert.IsType<RedirectResult>(await controller.Callback("code", "good", null, default)).Url);
        Assert.Equal($"{Channels}?tiktok=cancelled",
            Assert.IsType<RedirectResult>(await controller.Callback(null, "good", "access_denied", default)).Url);
        Assert.Equal($"{Channels}?tiktok=failed&reason={Uri.EscapeDataString("This TikTok authorization has expired. Start again.")}",
            Assert.IsType<RedirectResult>(await controller.Callback("code", "bad", null, default)).Url);

        // An unexpected failure is logged, not shown.
        string url = Assert.IsType<RedirectResult>(await controller.Callback("code", "broken", null, default)).Url;
        Assert.StartsWith($"{Channels}?tiktok=failed&reason=", url);
        Assert.DoesNotContain("secret", url);
    }

    private TikTokConnectionService NewService() =>
        new(_repository, _protector, _tikTok, Options.Create(new TikTokSettings { WebhookBaseUrl = "https://api.example.com/" }));

    private static TikTokAppRequest App(string appId, string appSecret) => new() { AppId = appId, AppSecret = appSecret };

    private static async Task<string> StateAsync(TikTokConnectionService service)
    {
        Uri url = new((await service.GetAuthorizationAsync(Owner)).AuthorizationUrl);
        return QueryHelpers.ParseQuery(url.Query)["state"].ToString();
    }

    private static TikTokSetupController Controller(ITikTokConnectionService connections, string? owner)
    {
        TikTokSetupController controller = new(connections,
            Options.Create(new FrontendSettings { BaseUrl = "https://app.example.com/" }),
            NullLogger<TikTokSetupController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        if (owner is not null)
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test"));
        return controller;
    }
}
