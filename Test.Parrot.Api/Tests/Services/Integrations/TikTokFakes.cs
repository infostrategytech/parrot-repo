using Parrot.Application.Integrations;
using Parrot.Domain.Entities;

namespace Test.Parrot.Api.Tests.Services.Integrations;

// Stand-ins for TikTok and the connection store, shared by the setup, webhook and reply tests.
internal sealed class FakeTikTok : ITikTokApiClient
{
    public Exception? Failure { get; set; }
    public Exception? RefreshFailure { get; set; }
    // Runs while a refresh is in flight, to stand in for another send renewing the same tokens.
    public Action? DuringRefresh { get; set; }
    public Exception? SendFailure { get; set; }
    public (string AppId, string AppSecret, string RefreshToken)? RefreshedWith { get; private set; }
    public TikTokTokens Tokens { get; set; } = new("business-open-id", TikTokConnectionService.Scopes.ToArray(),
        "access-token", TimeSpan.FromHours(24), "refresh-token", TimeSpan.FromDays(30));
    public TikTokAccount Account { get; set; } = new("parrot_test", "Parrot Test");
    public TikTokRefreshedTokens Refreshed { get; set; } = new("refreshed-access-token", TimeSpan.FromHours(24),
        "rotated-refresh-token", TimeSpan.FromDays(30));
    public int Refreshes { get; private set; }
    public List<(string BusinessId, string AccessToken, string ConversationId, string Text)> Sent { get; } = [];
    public string SentMessageId { get; set; } = "tiktok-message-1";
    public (string AppId, string AppSecret, string CallbackUrl)? RegisteredWebhook { get; private set; }
    public (string AppId, string AppSecret, string Code, string RedirectUri)? Exchanged { get; private set; }
    public (string BusinessId, string AccessToken)? AccountRequested { get; private set; }

    public Task RegisterWebhookAsync(string appId, string appSecret, string callbackUrl, CancellationToken cancellationToken)
    {
        if (Failure is not null) throw Failure;
        RegisteredWebhook = (appId, appSecret, callbackUrl);
        return Task.CompletedTask;
    }

    public Task<TikTokTokens> ExchangeCodeAsync(string appId, string appSecret, string code, string redirectUri,
        CancellationToken cancellationToken)
    {
        Exchanged = (appId, appSecret, code, redirectUri);
        return Task.FromResult(Tokens);
    }

    public Task<TikTokRefreshedTokens> RefreshTokensAsync(string appId, string appSecret, string refreshToken,
        CancellationToken cancellationToken)
    {
        Refreshes++;
        DuringRefresh?.Invoke();
        if (RefreshFailure is not null) throw RefreshFailure;
        RefreshedWith = (appId, appSecret, refreshToken);
        return Task.FromResult(Refreshed);
    }

    public Task<string> SendTextAsync(string businessId, string accessToken, string conversationId, string text,
        CancellationToken cancellationToken)
    {
        Sent.Add((businessId, accessToken, conversationId, text));
        if (SendFailure is not null) throw SendFailure;
        return Task.FromResult(SentMessageId);
    }

    public Task<TikTokAccount> GetAccountAsync(string businessId, string accessToken, CancellationToken cancellationToken)
    {
        AccountRequested = (businessId, accessToken);
        return Task.FromResult(Account);
    }
}

internal sealed class MemoryTikTokRepository : ITikTokConnectionRepository
{
    public TikTokConnection? Connection { get; private set; }

    public Task AddAsync(TikTokConnection connection, CancellationToken cancellationToken = default)
    {
        Connection = connection;
        return Task.CompletedTask;
    }

    // Like the real one: a disabled connection is not the owner's connection any more.
    public Task<TikTokConnection?> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Connection?.OwnerUserId == ownerUserId && Connection.DisabledAt is null ? Connection : null);

    public Task<TikTokConnection?> GetActiveByIdentifierAsync(string identifier, CancellationToken cancellationToken = default) =>
        Task.FromResult(Connection?.WebhookIdentifier == identifier && Connection.DisabledAt is null ? Connection : null);

    public Task<bool> DisableAsync(Guid id, string ownerUserId, DateTime disabledAt, CancellationToken cancellationToken = default)
    {
        if (Connection?.Id != id || Connection.OwnerUserId != ownerUserId || Connection.DisabledAt is not null)
            return Task.FromResult(false);
        Connection.DisabledAt = disabledAt;
        return Task.FromResult(true);
    }

    // Like the real one: only while the stored refresh token is still the one the caller used.
    public Task<bool> SaveRefreshedTokensAsync(Guid id, string expectedEncryptedRefreshToken, string encryptedAccessToken,
        DateTime accessTokenExpiresAt, string encryptedRefreshToken, DateTime refreshTokenExpiresAt,
        CancellationToken cancellationToken = default)
    {
        if (Connection?.Id != id || Connection.DisabledAt is not null || Connection.RefreshTokenEncrypted != expectedEncryptedRefreshToken)
            return Task.FromResult(false);
        Connection.AccessTokenEncrypted = encryptedAccessToken;
        Connection.AccessTokenExpiresAt = accessTokenExpiresAt;
        Connection.RefreshTokenEncrypted = encryptedRefreshToken;
        Connection.RefreshTokenExpiresAt = refreshTokenExpiresAt;
        return Task.FromResult(true);
    }

    public Task SaveAccountAsync(Guid id, string ownerUserId, string tikTokOpenId, string displayName,
        string encryptedAccessToken, DateTime accessTokenExpiresAt, string encryptedRefreshToken,
        DateTime refreshTokenExpiresAt, CancellationToken cancellationToken = default)
    {
        Connection!.TikTokOpenId = tikTokOpenId;
        Connection.DisplayName = displayName;
        Connection.AccessTokenEncrypted = encryptedAccessToken;
        Connection.AccessTokenExpiresAt = accessTokenExpiresAt;
        Connection.RefreshTokenEncrypted = encryptedRefreshToken;
        Connection.RefreshTokenExpiresAt = refreshTokenExpiresAt;
        return Task.CompletedTask;
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
