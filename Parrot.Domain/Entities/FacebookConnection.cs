namespace Parrot.Domain.Entities;

public sealed class FacebookConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }
    public required string VerifyTokenEncrypted { get; set; }
    public string? PageId { get; set; }
    public string? PageName { get; set; }
    public string? AccessTokenEncrypted { get; set; }
    public DateTime? WebhookVerifiedAt { get; set; }
    public DateTime? DisabledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
