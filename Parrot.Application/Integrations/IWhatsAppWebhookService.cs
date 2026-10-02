using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

public interface IWhatsAppWebhookService
{
    string GetWebhookUrl();
    bool TryVerifyChallenge(string hubMode, string hubVerifyToken, string hubChallenge, out string echoChallenge);
    bool IsValidSignature(string rawBody, string signatureHeader);

    // rawBody is the request body exactly as received; it travels to Kafka unchanged.
    Task ProcessEventAsync(WhatsAppWebhookPayload payload, string rawBody, CancellationToken cancellationToken = default);
    Task<bool> ProcessConnectionEventAsync(
        string identifier,
        WhatsAppWebhookPayload payload,
        string rawBody,
        CancellationToken cancellationToken = default);
}
