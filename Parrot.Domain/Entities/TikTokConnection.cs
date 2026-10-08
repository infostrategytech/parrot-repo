using System.Security.Cryptography;

namespace Parrot.Domain.Entities;

// A customer's link to one TikTok Business Account through their own TikTok app (manual setup):
// the app credentials that sign webhooks and refresh tokens, plus the tokens used to send replies.
public sealed class TikTokConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }

    // Per-connection webhook reference. The callback URL registered with TikTok carries it as ?ref=,
    // so events route by an indexed lookup before the signature is checked with this app's secret.
    public string WebhookIdentifier { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    // The App ID (client key) and App Secret of the customer's own TikTok app. TikTok signs webhooks
    // with the secret, and both are needed to refresh the access token. Callers must encrypt the secret.
    public required string AppId { get; set; }
    public required string AppSecretEncrypted { get; set; }

    // The TikTok Business Account, set once the customer authorizes their app.
    public string? TikTokOpenId { get; set; }
    public string? DisplayName { get; set; }

    // TikTok access tokens expire after about a day and are renewed with the refresh token, which
    // itself expires. Both are encrypted like the App Secret.
    public string? AccessTokenEncrypted { get; set; }
    public DateTime? AccessTokenExpiresAt { get; set; }
    public string? RefreshTokenEncrypted { get; set; }
    public DateTime? RefreshTokenExpiresAt { get; set; }

    public DateTime? DisabledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
