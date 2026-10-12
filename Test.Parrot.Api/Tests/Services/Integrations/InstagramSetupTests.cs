using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Parrot.Application.Conversations;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Conversations;

namespace Test.Parrot.Api.Tests.Services.Integrations;

// Manual Instagram setup: callback URL and verify token into Meta, then the access token and the
// Instagram app secret into Parrot. The account is read from the token, never typed in.
public class InstagramSetupTests
{
    private const string AccountId = "17841400000000000";
    private const string Secret = "0123456789abcdef0123456789abcdef";

    private readonly MemoryRepository _repository = new();
    private readonly FakeInstagram _instagram = new();

    private InstagramConnectionService Service() =>
        new(_repository, new Protector(), _instagram, Options.Create(new InstagramSettings { WebhookBaseUrl = "https://api.example.com/" }));

    private async Task<InstagramSetupResponse> VerifiedAsync()
    {
        InstagramConnectionService service = Service();
        InstagramSetupResponse setup = await service.GetSetupAsync("owner");
        Assert.True(await service.VerifyWebhookAsync(_repository.Connection!.WebhookIdentifier, "subscribe", setup.VerifyToken));
        return setup;
    }

    [Fact]
    public async Task Setup_GivesThisConnectionsOwnCallbackUrlAndVerifyToken_AndKeepsThem()
    {
        InstagramSetupResponse first = await Service().GetSetupAsync("owner");
        InstagramSetupResponse again = await Service().GetSetupAsync("owner");

        Assert.Equal($"https://api.example.com/api/webhooks/instagram?ref={_repository.Connection!.WebhookIdentifier}", first.WebhookUrl);
        Assert.Equal(first.VerifyToken, again.VerifyToken);
        Assert.Equal($"enc:{first.VerifyToken}", _repository.Connection.VerifyTokenEncrypted);
        Assert.False(first.WebhookVerified);
    }

    [Fact]
    public async Task Verification_NeedsThisConnectionsReferenceAndToken()
    {
        InstagramConnectionService service = Service();
        InstagramSetupResponse setup = await service.GetSetupAsync("owner");

        Assert.False(await service.VerifyWebhookAsync("another-ref", "subscribe", setup.VerifyToken));
        Assert.False(await service.VerifyWebhookAsync(_repository.Connection!.WebhookIdentifier, "subscribe", "wrong-token"));
        Assert.False(await service.VerifyWebhookAsync(_repository.Connection.WebhookIdentifier, "unsubscribe", setup.VerifyToken));
        Assert.True(await service.VerifyWebhookAsync(_repository.Connection.WebhookIdentifier, "subscribe", setup.VerifyToken));
        Assert.True((await service.GetSetupAsync("owner")).WebhookVerified);
    }

    // Webhooks carry the account's user_id, so it is read from the token, and the account is
    // subscribed to message webhooks, which businesses otherwise forget.
    [Fact]
    public async Task SavingTheToken_ReadsTheAccount_SubscribesIt_AndStoresTheTokenEncrypted()
    {
        await VerifiedAsync();

        await Service().SaveAccountAsync("owner", new InstagramAccountRequest { AccessToken = " IGAAtoken " });

        Assert.Equal(["account:IGAAtoken", "subscribe:IGAAtoken"], _instagram.Calls);
        Assert.Equal(AccountId, _repository.Connection!.AccountId);
        Assert.Equal("omaskitchen", _repository.Connection.Username);
        Assert.Equal("enc:IGAAtoken", _repository.Connection.AccessTokenEncrypted);
        InstagramSetupResponse setup = await Service().GetSetupAsync("owner");
        Assert.True(setup.HasAccessToken);
        Assert.DoesNotContain("IGAAtoken", JsonSerializer.Serialize(setup));
    }

    [Fact]
    public async Task ATokenInstagramRefuses_IsNotSaved_AndSaysWhy()
    {
        await VerifiedAsync();
        _instagram.Refuse = "Instagram did not accept this access token: it is wrong or has expired. Generate a new one.";

        ValidationException error = await Assert.ThrowsAsync<ValidationException>(() =>
            Service().SaveAccountAsync("owner", new InstagramAccountRequest { AccessToken = "bad" }));

        Assert.Contains("expired", error.Message);
        Assert.Null(_repository.Connection!.AccessTokenEncrypted);
    }

    [Fact]
    public async Task SavingTheToken_BeforeVerification_IsRefused()
    {
        await Service().GetSetupAsync("owner");

        await Assert.ThrowsAsync<ConflictException>(() =>
            Service().SaveAccountAsync("owner", new InstagramAccountRequest { AccessToken = "IGAAtoken" }));
        Assert.Empty(_instagram.Calls);
    }

    [Fact]
    public async Task TheAppSecret_IsStoredEncrypted_AndOnlyReportedAsSaved()
    {
        await Service().GetSetupAsync("owner");

        await Service().SaveAppSecretAsync("owner", new AppSecretRequest { AppSecret = Secret });

        Assert.Equal($"enc:{Secret}", _repository.Connection!.AppSecretEncrypted);
        InstagramSetupResponse setup = await Service().GetSetupAsync("owner");
        Assert.True(setup.HasAppSecret);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(setup));
    }

    // The secret is easy to confuse with the App Secret under App settings > Basic, so the error says where to look.
    [Fact]
    public async Task AnythingButA32CharacterSecret_IsRefused_PointingAtTheRightPlace()
    {
        await Service().GetSetupAsync("owner");

        ValidationException error = await Assert.ThrowsAsync<ValidationException>(() =>
            Service().SaveAppSecretAsync("owner", new AppSecretRequest { AppSecret = "1234567890" }));

        Assert.Contains("API setup with Instagram login", error.Message);
        Assert.Null(_repository.Connection!.AppSecretEncrypted);
    }

    [Fact]
    public async Task Disconnect_StopsTheOldReference_AndSetupStartsAFreshConnection()
    {
        InstagramSetupResponse first = await VerifiedAsync();
        string oldReference = _repository.Connection!.WebhookIdentifier;

        await Service().DisableAsync("owner");

        Assert.Null(await _repository.GetActiveByIdentifierAsync(oldReference));
        InstagramSetupResponse again = await Service().GetSetupAsync("owner");
        Assert.NotEqual(first.WebhookUrl, again.WebhookUrl);
        Assert.False(again.WebhookVerified);
    }

    [Fact]
    public async Task Setup_NeedsAnHttpsCallbackBase()
    {
        InstagramConnectionService service = new(_repository, new Protector(), _instagram,
            Options.Create(new InstagramSettings { WebhookBaseUrl = "http://api.example.com" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSetupAsync("owner"));
        Assert.Null(_repository.Connection);
    }

    // graph.instagram.com: the two setup calls and the Send API.
    [Theory]
    [InlineData("""{"user_id":17841400000000000,"username":"omaskitchen","id":"28747265801525964"}""")]
    [InlineData("""{"user_id":"17841400000000000","username":"omaskitchen"}""")]
    public async Task TheAccountLookup_ReadsUserId_NotTheTokensOwnId(string body)
    {
        Handler handler = new(HttpStatusCode.OK, body);
        InstagramAccountApi api = new(new HttpClient(handler), Options.Create(new InstagramSettings { ApiVersion = "v25.0" }));

        InstagramAccount account = await api.GetAccountAsync("IGAAtoken", default);

        Assert.Equal(new InstagramAccount(AccountId, "omaskitchen"), account);
        Assert.Equal("https://graph.instagram.com/v25.0/me?fields=user_id,username", handler.Url);
        Assert.Equal("Bearer IGAAtoken", handler.Authorization);
    }

    [Fact]
    public async Task AnExpiredToken_IsExplained()
    {
        InstagramAccountApi api = new(new HttpClient(new Handler(HttpStatusCode.BadRequest, """{"error":{"code":190}}""")),
            Options.Create(new InstagramSettings()));

        InstagramAccountException error = await Assert.ThrowsAsync<InstagramAccountException>(() => api.GetAccountAsync("old", default));
        Assert.Contains("expired", error.Message);
    }

    [Fact]
    public async Task Subscribing_PostsTheMessagesField()
    {
        Handler handler = new(HttpStatusCode.OK, """{"success":true}""");
        InstagramAccountApi api = new(new HttpClient(handler), Options.Create(new InstagramSettings { ApiVersion = "v25.0" }));

        await api.SubscribeToMessagesAsync("IGAAtoken", default);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://graph.instagram.com/v25.0/me/subscribed_apps?subscribed_fields=messages", handler.Url);
    }

    [Theory]
    [InlineData(200, """{"recipient_id":"5834920000000000","message_id":"aWdfZAG1f"}""", "accepted")]
    [InlineData(400, """{"error":{"code":10,"error_subcode":2534022}}""", "failed")]
    [InlineData(500, "{}", "unknown")]
    [InlineData(200, "{}", "unknown")]
    public async Task TheSendApi_IsCalledOnceWithTheAccountAndCustomer(int status, string body, string outcome)
    {
        Handler handler = new((HttpStatusCode)status, body);
        InstagramSender sender = new(new HttpClient(handler), Options.Create(new InstagramSettings { ApiVersion = "v25.0" }));

        if (outcome == "accepted") Assert.Equal("aWdfZAG1f", await sender.SendAsync(AccountId, "IGAAtoken", "5834920000000000", "Hello", default));
        else if (outcome == "failed")
            Assert.Equal("10", (await Assert.ThrowsAsync<InstagramRejectedException>(() => sender.SendAsync(AccountId, "IGAAtoken", "5834920000000000", "Hello", default))).MetaErrorCode);
        else await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(AccountId, "IGAAtoken", "5834920000000000", "Hello", default));

        Assert.Equal(1, handler.Calls);
        Assert.Equal($"https://graph.instagram.com/v25.0/{AccountId}/messages", handler.Url);
        Assert.Equal("Bearer IGAAtoken", handler.Authorization);
        using JsonDocument json = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("5834920000000000", json.RootElement.GetProperty("recipient").GetProperty("id").GetString());
        Assert.Equal("Hello", json.RootElement.GetProperty("message").GetProperty("text").GetString());
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Method = request.Method; Url = request.RequestUri!.ToString(); Authorization = request.Headers.Authorization?.ToString();
            Payload = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeInstagram : IInstagramAccountApi
    {
        public List<string> Calls { get; } = [];
        public string? Refuse { get; set; }

        public Task<InstagramAccount> GetAccountAsync(string accessToken, CancellationToken cancellationToken)
        {
            Calls.Add($"account:{accessToken}");
            if (Refuse is not null) throw new InstagramAccountException(Refuse);
            return Task.FromResult(new InstagramAccount(AccountId, "omaskitchen"));
        }

        public Task SubscribeToMessagesAsync(string accessToken, CancellationToken cancellationToken)
        {
            Calls.Add($"subscribe:{accessToken}");
            return Task.CompletedTask;
        }
    }

    private sealed class Protector : IInstagramCredentialProtector
    {
        public string Protect(string value) => $"enc:{value}";
        public string Unprotect(string protectedValue) => protectedValue[4..];
    }

    // Like the real one: a disabled connection is not the owner's connection any more.
    private sealed class MemoryRepository : IInstagramConnectionRepository
    {
        public InstagramConnection? Connection { get; private set; }

        public Task<InstagramConnection?> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Connection?.OwnerUserId == ownerUserId && Connection.DisabledAt is null ? Connection : null);

        public Task<InstagramConnection?> GetActiveByIdentifierAsync(string identifier, CancellationToken cancellationToken = default) =>
            Task.FromResult(Connection?.WebhookIdentifier == identifier && Connection.DisabledAt is null ? Connection : null);

        public Task AddAsync(InstagramConnection connection, CancellationToken cancellationToken = default)
        {
            Connection = connection;
            return Task.CompletedTask;
        }

        public Task<bool> MarkVerifiedAsync(Guid id, DateTime verifiedAt, CancellationToken cancellationToken = default)
        {
            if (Connection?.Id != id || Connection.DisabledAt is not null) return Task.FromResult(false);
            Connection.WebhookVerifiedAt ??= verifiedAt;
            return Task.FromResult(true);
        }

        public Task SaveAccountAsync(Guid id, string ownerUserId, string accountId, string username, string encryptedAccessToken,
            CancellationToken cancellationToken = default)
        {
            Connection!.AccountId = accountId;
            Connection.Username = username;
            Connection.AccessTokenEncrypted = encryptedAccessToken;
            return Task.CompletedTask;
        }

        public Task SaveAppSecretAsync(Guid id, string ownerUserId, string encryptedSecret, CancellationToken cancellationToken = default)
        {
            Connection!.AppSecretEncrypted = encryptedSecret;
            return Task.CompletedTask;
        }

        public Task<bool> IsOwnedByAsync(Guid id, string ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Connection?.Id == id && Connection.OwnerUserId == ownerUserId);

        public Task<bool> DisableAsync(Guid id, string ownerUserId, DateTime disabledAt, CancellationToken cancellationToken = default)
        {
            if (Connection?.Id != id || Connection.DisabledAt is not null) return Task.FromResult(false);
            Connection.DisabledAt = disabledAt;
            return Task.FromResult(true);
        }
    }
}
