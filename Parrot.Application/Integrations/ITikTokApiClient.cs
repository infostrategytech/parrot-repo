namespace Parrot.Application.Integrations;

// The TikTok API for Business calls setup makes, with the customer's own app credentials or tokens.
public interface ITikTokApiClient
{
    // Points the app's Business Messaging (DIRECT_MESSAGE) webhook at callbackUrl. TikTok refuses
    // an App ID and secret that don't match, so this also checks them.
    Task RegisterWebhookAsync(string appId, string appSecret, string callbackUrl, CancellationToken cancellationToken);

    // Exchanges the code TikTok sent to redirectUri for the Business Account's tokens.
    Task<TikTokTokens> ExchangeCodeAsync(string appId, string appSecret, string code, string redirectUri,
        CancellationToken cancellationToken);

    // Reads the Business Account's username and display name.
    Task<TikTokAccount> GetAccountAsync(string businessId, string accessToken, CancellationToken cancellationToken);

    // Trades a refresh token for a new access token; TikTok may also rotate the refresh token.
    Task<TikTokRefreshedTokens> RefreshTokensAsync(string appId, string appSecret, string refreshToken,
        CancellationToken cancellationToken);

    // Sends a text reply into a conversation and returns TikTok's id for the message. A refusal is a
    // TikTokApiException; a 5xx or no answer leaves it unknown whether the message went, so is not retried.
    Task<string> SendTextAsync(string businessId, string accessToken, string conversationId, string text,
        CancellationToken cancellationToken);
}

// TikTok's token response. The access token lasts about a day; the refresh token renews it until it expires too.
public sealed record TikTokTokens(
    string OpenId,
    IReadOnlyList<string> Scopes,
    string AccessToken,
    TimeSpan AccessTokenLifetime,
    string RefreshToken,
    TimeSpan RefreshTokenLifetime);

public sealed record TikTokRefreshedTokens(
    string AccessToken,
    TimeSpan AccessTokenLifetime,
    string RefreshToken,
    TimeSpan RefreshTokenLifetime);

public sealed record TikTokAccount(string Username, string? DisplayName);

// TikTok answered but refused the call: an HTTP 4xx, or a non-zero "code" in its response. The message
// is TikTok's own and safe to show the customer. Failures to reach TikTok are HttpRequestException.
public sealed class TikTokApiException(long code, string message) : Exception(message)
{
    public long Code { get; } = code;
}
