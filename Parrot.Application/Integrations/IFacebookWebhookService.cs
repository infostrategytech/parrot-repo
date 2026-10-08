using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

// Inbound Messenger webhook handling. Every event must carry a ?ref= connection reference.
public interface IFacebookWebhookService
{
    // Base callback URL without the ?ref= suffix; the per-connection URL comes from IFacebookConnectionService.
    string GetWebhookUrl();

    // Checks X-Hub-Signature-256 with the connection's own App Secret (manual setup) or the
    // app-level one (Parrot's app), as Facebook:SignatureMode says. False means answer 401.
    Task<bool> IsAuthenticAsync(string? identifier, string rawBody, string? signature,
        CancellationToken cancellationToken = default);

    // Routes an event to the connection named by ?ref=; false means reject with 403.
    // rawBody is the request body exactly as received; it travels to Kafka unchanged.
    Task<bool> ProcessConnectionEventAsync(string identifier, FacebookWebhookPayload payload, string rawBody,
        CancellationToken cancellationToken = default);
}
