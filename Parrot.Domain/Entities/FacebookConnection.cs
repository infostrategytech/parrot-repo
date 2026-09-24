namespace Parrot.Domain.Entities;

// A customer's link to one Facebook Page: webhook credentials plus the Page token used to send replies.
public sealed class FacebookConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }
    // Callers must encrypt before assigning; never store plaintext here.
    public required string VerifyTokenEncrypted { get; set; }
    public string? PageId { get; set; }
    public string? PageName { get; set; }
    // Page access token (not a user token); encrypted by IFacebookCredentialProtector.
    public string? AccessTokenEncrypted { get; set; }
    public DateTime? WebhookVerifiedAt { get; set; }
    public DateTime? DisabledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
