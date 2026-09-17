namespace Parrot.Application.DTOs.Integrations;

public class WhatsAppConnectionResponse
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string SetupStatus { get; init; }
    public string? WhatsAppBusinessAccountId { get; init; }
    public string? PhoneNumberId { get; init; }
    public string? DisplayPhoneNumber { get; init; }
    public DateTime? WebhookVerifiedAt { get; init; }
    public DateTime? FirstMessageReceivedAt { get; init; }
    public DateTime? DisabledAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
