using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

// The two graph.instagram.com calls manual setup makes with the business's token.
public interface IInstagramAccountApi
{
    // GET /me?fields=user_id,username: the professional account id that webhooks carry in entry.id.
    // Throws InstagramAccountException when Instagram refuses the token.
    Task<InstagramAccount> GetAccountAsync(string accessToken, CancellationToken cancellationToken);

    // POST /me/subscribed_apps?subscribed_fields=messages: without it Meta sends no message webhooks.
    Task SubscribeToMessagesAsync(string accessToken, CancellationToken cancellationToken);

    // GET /refresh_access_token?grant_type=ig_refresh_token: a new token good for another 60 days.
    // Only for a token at least a day old that has not expired. Throws InstagramAccountException.
    Task<InstagramRenewedToken> RenewTokenAsync(string accessToken, CancellationToken cancellationToken);
}

public sealed record InstagramRenewedToken(string AccessToken, TimeSpan ExpiresIn);

// Code is Meta's error.code when Instagram answered: 190 means the token itself is no good any more.
public sealed class InstagramAccountException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;

    public bool TokenRejected => Code == "190";
}
