using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

// Inbound Messenger webhook handling. Every event must carry a ?ref= connection reference.
public interface IFacebookWebhookService
{
    // Base callback URL without the ?ref= suffix; the per-connection URL comes from IFacebookConnectionService.
    string GetWebhookUrl();

    // Verifies X-Hub-Signature-256 against the app-level App Secret shared by all connections.
    bool IsValidSignature(string rawBody, string signatureHeader);

    // Routes an event to the connection named by ?ref=; false means reject with 403.
    Task<bool> ProcessConnectionEventAsync(string identifier, FacebookWebhookPayload payload,
        CancellationToken cancellationToken = default);
}
