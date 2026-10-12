using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

// Inbound Instagram DMs. Every event carries a ?ref= naming its connection.
public interface IInstagramWebhookService
{
    // X-Hub-Signature-256 with the connection's Instagram app secret (or the app-level one), as
    // Instagram:SignatureMode says. False means answer 401.
    Task<bool> IsAuthenticAsync(string? identifier, string rawBody, string? signature, CancellationToken cancellationToken = default);

    // Routes the event to the connection named by ?ref=; false means reject with 403. Instagram's
    // payload has Messenger's shape (object "instagram"), so it binds to FacebookWebhookPayload.
    Task<bool> ProcessConnectionEventAsync(string identifier, FacebookWebhookPayload payload, string rawBody,
        CancellationToken cancellationToken = default);
}
