using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

public interface IWhatsAppWebhookService
{
    string GetWebhookUrl();
    bool TryVerifyChallenge(string hubMode, string hubVerifyToken, string hubChallenge, out string echoChallenge);

    // Checks X-Hub-Signature-256 with the connection's own App Secret (manual setup) or the
    // app-level one (Parrot's app), as WhatsApp:SignatureMode says. False means answer 401.
    Task<bool> IsAuthenticAsync(string? identifier, string rawBody, string? signature,
        CancellationToken cancellationToken = default);

    // rawBody is the request body exactly as received; it travels to Kafka unchanged.
    Task ProcessEventAsync(WhatsAppWebhookPayload payload, string rawBody, CancellationToken cancellationToken = default);
    Task<bool> ProcessConnectionEventAsync(
        string identifier,
        WhatsAppWebhookPayload payload,
        string rawBody,
        CancellationToken cancellationToken = default);
}
