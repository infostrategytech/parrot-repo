namespace Parrot.Application.DTOs.Integrations;

public class WhatsAppConnectionResponse
{
    public bool HasAccessToken { get; init; }

    /// <summary>A manual connection's own App Secret is saved; its webhooks are checked with it.</summary>
    public bool HasAppSecret { get; init; }
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
