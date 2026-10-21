using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Rest.Controllers;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class FacebookSetupTests
{
    [Fact]
    public async Task SetupReturnsOwnedConnectionAndWebhookVerificationUsesConnectionService()
    {
        Mock<IFacebookConnectionService> connections = new();
        FacebookSetupResponse expected = new(Guid.NewGuid(), "https://api.example.com/api/webhooks/facebook?ref=abc",
            "verify-token", false, null, null, false, false);
        connections.Setup(x => x.GetSetupAsync("owner", default)).ReturnsAsync(expected);
        connections.Setup(x => x.VerifyWebhookAsync("abc", "subscribe", "verify-token", default)).ReturnsAsync(true);
        FacebookSetupController setup = new(connections.Object);
        setup.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        setup.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "owner") }, "test"));
        OkObjectResult response = Assert.IsType<OkObjectResult>(await setup.Get(default));
        Assert.Same(expected, response.Value);

        FacebookWebhookController webhook = new(Mock.Of<IFacebookWebhookService>(), connections.Object);
        webhook.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ContentResult result = Assert.IsType<ContentResult>(
            await webhook.VerifyWebhook("abc", "subscribe", "verify-token", "challenge"));
        Assert.Equal("challenge", result.Content);
    }

    [Fact]
    public async Task WebhookVerificationIsRejectedWhenTheReferenceIsMissingOrUnknown()
    {
        Mock<IFacebookConnectionService> connections = new();
        connections.Setup(x => x.VerifyWebhookAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync(false);
        FacebookWebhookController webhook = new(Mock.Of<IFacebookWebhookService>(), connections.Object);
        webhook.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        StatusCodeResult result = Assert.IsType<StatusCodeResult>(
            await webhook.VerifyWebhook(null, "subscribe", "verify-token", "challenge"));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task SetupRejectsMissingOwner()
    {
        FacebookSetupController controller = new(Mock.Of<IFacebookConnectionService>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        Assert.IsType<UnauthorizedResult>(await controller.Get(default));
    }

    [Fact]
    public async Task ConnectionFlowPersistsVerificationAndEncryptedPageToken()
    {
        MemoryFacebookRepository repository = new();
        FacebookConnectionService service = NewService(repository);

        FacebookSetupResponse setup = await service.GetSetupAsync("owner");
        Assert.False(setup.WebhookVerified);

        // The callback URL must carry this connection's own reference.
        string identifier = repository.Connection!.WebhookIdentifier;
        Assert.Equal($"https://api.example.com/api/webhooks/facebook?ref={identifier}", setup.WebhookUrl);

        Assert.True(await service.VerifyWebhookAsync(identifier, "subscribe", setup.VerifyToken));

        await service.SavePageAsync("owner", new FacebookPageRequest
        {
            PageId = "123456",
            PageName = "Support Page",
            AccessToken = "page-secret",
        });

        Assert.Equal("enc:page-secret", repository.Connection!.AccessTokenEncrypted);
        Assert.Equal("123456", repository.Connection.PageId);
    }

    // Manual setup: allowed before verification, so it can be entered with the callback URL.
    [Fact]
    public async Task TheAppSecretIsStoredEncrypted_AndOnlyReportedAsSaved()
    {
        MemoryFacebookRepository repository = new();
        FacebookConnectionService service = NewService(repository);
        Assert.False((await service.GetSetupAsync("owner")).HasAppSecret);

        await service.SaveAppSecretAsync("owner", new AppSecretRequest { AppSecret = "0123456789abcdef0123456789abcdef" });

        Assert.Equal("enc:0123456789abcdef0123456789abcdef", repository.Connection!.AppSecretEncrypted);
        FacebookSetupResponse setup = await service.GetSetupAsync("owner");
        Assert.True(setup.HasAppSecret);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", System.Text.Json.JsonSerializer.Serialize(setup));
    }

    [Fact]
    public async Task AnAppIdOrOtherValue_IsNotAcceptedAsTheAppSecret()
    {
        MemoryFacebookRepository repository = new();
        FacebookConnectionService service = NewService(repository);
        await service.GetSetupAsync("owner");

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.SaveAppSecretAsync("owner", new AppSecretRequest { AppSecret = "185860297939218" }));
        Assert.Null(repository.Connection!.AppSecretEncrypted);
    }

    // Disconnecting stops the Page's webhooks at once; setting up again gets a new callback URL.
    [Fact]
    public async Task DisconnectStopsTheOldReference_AndSetupStartsAFreshConnection()
    {
        MemoryFacebookRepository repository = new();
        FacebookConnectionService service = NewService(repository);
        FacebookSetupResponse first = await service.GetSetupAsync("owner");
        string oldReference = repository.Connection!.WebhookIdentifier;
        Assert.True(await service.VerifyWebhookAsync(oldReference, "subscribe", first.VerifyToken));

        await service.DisableAsync("owner");

        Assert.Null(await repository.GetActiveByIdentifierAsync(oldReference));
        Assert.False(await service.VerifyWebhookAsync(oldReference, "subscribe", first.VerifyToken));
        FacebookSetupResponse again = await service.GetSetupAsync("owner");
        Assert.NotEqual(first.ConnectionId, again.ConnectionId);
        Assert.NotEqual(first.WebhookUrl, again.WebhookUrl);
        Assert.False(again.WebhookVerified);
    }

    [Fact]
    public async Task DisconnectWithNothingConnected_IsNotFound()
    {
        FacebookConnectionService service = NewService(new MemoryFacebookRepository());

        await Assert.ThrowsAsync<NotFoundException>(() => service.DisableAsync("owner"));
    }

    // The card offers Disconnect only where the backend can really stop the messages.
    [Theory]
    [InlineData(global::Parrot.Web.Features.Dashboard.Channels.Models.ChannelId.Whatsapp, true)]
    [InlineData(global::Parrot.Web.Features.Dashboard.Channels.Models.ChannelId.Facebook, true)]
    [InlineData(global::Parrot.Web.Features.Dashboard.Channels.Models.ChannelId.Instagram, true)]
    [InlineData(global::Parrot.Web.Features.Dashboard.Channels.Models.ChannelId.X, false)]
    public void DisconnectIsOfferedWhereTheBackendCanDisable(global::Parrot.Web.Features.Dashboard.Channels.Models.ChannelId channel, bool expected) =>
        Assert.Equal(expected, new global::Parrot.Web.Features.Dashboard.Channels.Services.LiveChannelCapabilities().CanDisconnect(channel));

    [Fact]
    public async Task VerificationFailsWhenTheReferenceDoesNotMatchTheToken()
    {
        MemoryFacebookRepository repository = new();
        FacebookConnectionService service = NewService(repository);
        FacebookSetupResponse setup = await service.GetSetupAsync("owner");
        string identifier = repository.Connection!.WebhookIdentifier;

        // Right reference, wrong token.
        Assert.False(await service.VerifyWebhookAsync(identifier, "subscribe", "not-the-token"));

        // Right token, unknown reference — one customer's token must not verify another's callback.
        Assert.False(await service.VerifyWebhookAsync("deadbeef", "subscribe", setup.VerifyToken));

        // Wrong hub.mode.
        Assert.False(await service.VerifyWebhookAsync(identifier, "unsubscribe", setup.VerifyToken));

        Assert.Null(repository.Connection.WebhookVerifiedAt);
    }

    [Fact]
    public async Task SetupFailsBeforeWritingWhenTheWebhookBaseUrlIsNotConfigured()
    {
        MemoryFacebookRepository repository = new();
        FacebookConnectionService service = new(repository, new TestProtector(),
            Options.Create(new FacebookSettings { WebhookBaseUrl = string.Empty }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSetupAsync("owner"));

        // A misconfigured server must not leave a half-set-up connection behind.
        Assert.Null(repository.Connection);
    }

    private static FacebookConnectionService NewService(MemoryFacebookRepository repository) =>
        new(repository, new TestProtector(),
            Options.Create(new FacebookSettings { WebhookBaseUrl = "https://api.example.com" }));

    private sealed class TestProtector : IFacebookCredentialProtector
    {
        public string Protect(string value) => $"enc:{value}";
        public string Unprotect(string protectedValue) => protectedValue[4..];
    }

    private sealed class MemoryFacebookRepository : IFacebookConnectionRepository
    {
        public FacebookConnection? Connection { get; private set; }

        public Task AddAsync(FacebookConnection connection, CancellationToken cancellationToken = default)
        {
            Connection = connection;
            return Task.CompletedTask;
        }

        // Like the real one: a disabled connection is not the owner's connection any more.
        public Task<FacebookConnection?> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Connection?.WorkspaceId == ownerUserId && Connection.DisabledAt is null ? Connection : null);

        public Task<bool> IsOwnedByAsync(Guid id, string ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Connection?.Id == id && Connection.WorkspaceId == ownerUserId);

        public Task<bool> DisableAsync(Guid id, string ownerUserId, DateTime disabledAt, CancellationToken cancellationToken = default)
        {
            if (Connection?.Id != id || Connection.WorkspaceId != ownerUserId || Connection.DisabledAt is not null)
                return Task.FromResult(false);
            Connection.DisabledAt = disabledAt;
            return Task.FromResult(true);
        }

        public Task<FacebookConnection?> GetActiveByIdentifierAsync(string identifier, CancellationToken cancellationToken = default) =>
            Task.FromResult(Connection?.WebhookIdentifier == identifier && Connection.DisabledAt is null ? Connection : null);

        public Task<bool> MarkVerifiedAsync(Guid id, DateTime verifiedAt, CancellationToken cancellationToken = default)
        {
            if (Connection?.Id != id) return Task.FromResult(false);
            Connection.WebhookVerifiedAt ??= verifiedAt;
            return Task.FromResult(true);
        }

        public Task SavePageAsync(Guid id, string ownerUserId, string pageId, string pageName,
            string encryptedAccessToken, CancellationToken cancellationToken = default)
        {
            Connection!.PageId = pageId;
            Connection.PageName = pageName;
            Connection.AccessTokenEncrypted = encryptedAccessToken;
            return Task.CompletedTask;
        }

        public Task SaveAppSecretAsync(Guid id, string ownerUserId, string encryptedSecret,
            CancellationToken cancellationToken = default)
        {
            Connection!.AppSecretEncrypted = encryptedSecret;
            return Task.CompletedTask;
        }
    }
}
