using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Integrations;

// Everything the Channels setup screen needs to render the current step. Credentials are write-only:
// only whether one is saved comes back.
public sealed record InstagramSetupResponse(
    Guid ConnectionId,
    string WebhookUrl,
    string VerifyToken,
    bool WebhookVerified,
    string? AccountId,
    string? Username,
    bool HasAccessToken,
    bool HasAppSecret);

// The Instagram User access token from the business's own Meta app (Instagram > API setup with
// Instagram login > Generate token). Parrot reads the account from it, so no id is typed in.
public sealed class InstagramAccountRequest
{
    [Required, StringLength(8192, MinimumLength = 1)]
    public required string AccessToken { get; set; }
}

// Who the access token belongs to, as webhooks and the Send API name the account.
public sealed record InstagramAccount(string AccountId, string Username);
