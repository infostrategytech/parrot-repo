using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Rest.Controllers;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class FacebookSetupTests
{
    [Fact]
    public async Task SetupReturnsOwnedConnectionAndWebhookVerificationUsesConnectionService()
    {
        Mock<IFacebookConnectionService> connections = new();
        FacebookSetupResponse expected = new(Guid.NewGuid(), "https://api.example.com/api/webhooks/facebook",
            "verify-token", false, null, null, false);
        connections.Setup(x => x.GetSetupAsync("owner", default)).ReturnsAsync(expected);
        connections.Setup(x => x.VerifyWebhookAsync("subscribe", "verify-token", default)).ReturnsAsync(true);
        FacebookSetupController setup = new(connections.Object);
        setup.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        setup.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "owner") }, "test"));
        OkObjectResult response = Assert.IsType<OkObjectResult>(await setup.Get(default));
        Assert.Same(expected, response.Value);

        FacebookWebhookController webhook = new(Mock.Of<IFacebookWebhookService>(), connections.Object);
        webhook.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ContentResult result = Assert.IsType<ContentResult>(await webhook.VerifyWebhook("subscribe", "verify-token", "challenge"));
        Assert.Equal("challenge", result.Content);
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
        TestProtector protector = new();
        FacebookConnectionService service = new(repository, protector,
            Options.Create(new FacebookSettings { WebhookBaseUrl = "https://api.example.com" }));

        FacebookSetupResponse setup = await service.GetSetupAsync("owner");
        Assert.False(setup.WebhookVerified);
        Assert.True(await service.VerifyWebhookAsync("subscribe", setup.VerifyToken));

        await service.SavePageAsync("owner", new FacebookPageRequest
        {
            PageId = "123456",
            PageName = "Support Page",
            AccessToken = "page-secret",
        });

        Assert.Equal("enc:page-secret", repository.Connection!.AccessTokenEncrypted);
        Assert.Equal("123456", repository.Connection.PageId);
        Assert.Equal("owner", (await repository.ResolveAsync("123456"))!.OwnerUserId);
    }

    private sealed class TestProtector : IFacebookCredentialProtector
    {
        public string Protect(string value) => $"enc:{value}";
        public string Unprotect(string protectedValue) => protectedValue[4..];
    }

    private sealed class MemoryFacebookRepository : IFacebookConnectionRepository, IFacebookVerifyTokenLookup, IFacebookPageRegistry
    {
        public FacebookConnection? Connection { get; private set; }
        public Task AddAsync(FacebookConnection connection, CancellationToken cancellationToken = default)
        {
            Connection = connection;
            return Task.CompletedTask;
        }
        public Task<FacebookConnection?> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Connection?.OwnerUserId == ownerUserId ? Connection : null);
        public Task<FacebookConnection?> GetByPageIdAsync(string pageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Connection?.PageId == pageId ? Connection : null);
        public Task<bool> MarkVerifiedAsync(Guid id, DateTime verifiedAt, CancellationToken cancellationToken = default)
        {
            if (Connection?.Id != id) return Task.FromResult(false);
            Connection.WebhookVerifiedAt = verifiedAt;
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
        public Task<FacebookConnection?> FindByVerifyTokenAsync(string verifyToken,
            IFacebookCredentialProtector protector, CancellationToken cancellationToken = default) =>
            Task.FromResult(Connection is not null && protector.Unprotect(Connection.VerifyTokenEncrypted) == verifyToken
                ? Connection : null);
        public Task<FacebookPageRegistration?> ResolveAsync(string pageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Connection?.PageId == pageId
                ? new FacebookPageRegistration(Connection.Id, Connection.OwnerUserId, pageId, Connection.PageName!) : null);
    }
}
