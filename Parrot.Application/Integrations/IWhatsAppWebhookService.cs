using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

public interface IWhatsAppWebhookService
{
    string GetWebhookUrl();
    bool TryVerifyChallenge(string hubMode, string hubVerifyToken, string hubChallenge, out string echoChallenge);
    bool IsValidSignature(string rawBody, string signatureHeader);
    Task ProcessEventAsync(WhatsAppWebhookPayload payload, CancellationToken cancellationToken = default);
    Task<bool> ProcessConnectionEventAsync(
        string identifier,
        WhatsAppWebhookPayload payload,
        CancellationToken cancellationToken = default);
}
