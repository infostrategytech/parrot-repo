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
}

public sealed class InstagramAccountException(string message) : Exception(message);
