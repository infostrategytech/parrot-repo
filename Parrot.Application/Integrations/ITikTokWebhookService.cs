namespace Parrot.Application.Integrations;

// Inbound TikTok webhook handling. Every event arrives at the callback URL of one connection.
public interface ITikTokWebhookService
{
    // Checks TikTok-Signature with the connection's own App Secret. False means answer 401.
    Task<bool> IsAuthenticAsync(string identifier, string rawBody, string? signature,
        CancellationToken cancellationToken = default);

    // Routes an authentic event to the connection named in the URL; false means reject with 403.
    // rawBody is the request body exactly as received; it travels to Kafka unchanged.
    Task<bool> ProcessEventAsync(string identifier, string rawBody, CancellationToken cancellationToken = default);
}
