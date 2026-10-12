using System.Security.Cryptography;

namespace Parrot.Domain.Entities;

// A customer's link to one Instagram professional account, set up by hand with their own Meta app
// (Instagram API with Instagram Login): webhook credentials, the account, and the token to reply with.
public sealed class InstagramConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }

    // Per-connection webhook reference. Meta echoes it back as ?ref=, so events route by an indexed lookup.
    public string WebhookIdentifier { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    // Callers must encrypt before assigning; never store plaintext here.
    public required string VerifyTokenEncrypted { get; set; }

    // The professional account's id as webhooks carry it in entry.id: GET /me?fields=user_id's user_id,
    // read from the access token, never typed in (the token exchange's own id is a different number).
    public string? AccountId { get; set; }
    public string? Username { get; set; }

    // Instagram User access token with instagram_business_manage_messages; encrypted.
    public string? AccessTokenEncrypted { get; set; }

    // The Instagram app secret (Instagram > API setup with Instagram login), which signs this
    // connection's webhooks. Not the Meta App Secret under App settings > Basic. Encrypted.
    public string? AppSecretEncrypted { get; set; }

    public DateTime? WebhookVerifiedAt { get; set; }
    public DateTime? DisabledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
