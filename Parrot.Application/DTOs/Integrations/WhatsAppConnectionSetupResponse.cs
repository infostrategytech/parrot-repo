namespace Parrot.Application.DTOs.Integrations;

public class WhatsAppConnectionSetupResponse
{
    public required WhatsAppConnectionResponse Connection { get; init; }
    public required string WebhookUrl { get; init; }
    public required string VerifyToken { get; init; }
}
