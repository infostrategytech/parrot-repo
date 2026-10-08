using System.Security.Cryptography;

namespace Parrot.Domain.Entities;

// A customer's link to one Facebook Page: webhook credentials plus the Page token used to send replies.
public sealed class FacebookConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }

    // Per-connection webhook reference. Meta echoes it back as ?ref=, so events route by an indexed
    // lookup instead of decrypting every stored verify token.
    public string WebhookIdentifier { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    // Callers must encrypt before assigning; never store plaintext here.
    public required string VerifyTokenEncrypted { get; set; }
    public string? PageId { get; set; }
    public string? PageName { get; set; }

    // Page access token (not a user token); encrypted by IFacebookCredentialProtector.
    public string? AccessTokenEncrypted { get; set; }

    // The App Secret of the customer's own Meta app, for a manually set up connection: Meta signs
    // its webhooks with it. Null for a connection made through Parrot's app (Embedded Signup), whose
    // webhooks are checked with the app-level Facebook:AppSecret. Encrypted like the access token.
    public string? AppSecretEncrypted { get; set; }
    public DateTime? WebhookVerifiedAt { get; set; }
    public DateTime? DisabledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
