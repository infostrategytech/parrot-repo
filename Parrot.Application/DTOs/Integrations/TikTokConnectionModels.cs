using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Integrations;

// Everything the TikTok setup screen needs to render the current step.
public sealed record TikTokSetupResponse(
    // Null until the customer saves their app's App ID and App Secret.
    Guid? ConnectionId,
    // Added to the customer's TikTok app as its redirect URI, before they authorize.
    string RedirectUri,
    string? AppId,
    bool Authorized,
    string? DisplayName,
    // Authorized once, but the refresh token has lapsed too, so messages cannot be sent until the owner authorizes again.
    bool NeedsReauthorization = false);

// The customer's own TikTok app (My Apps > App Detail > Basic Information). Write-only: responses
// never include the secret.
public sealed class TikTokAppRequest
{
    [Required, StringLength(64, MinimumLength = 1)]
    public required string AppId { get; set; }

    [Required, StringLength(128, MinimumLength = 1)]
    public required string AppSecret { get; set; }
}

// Where to send the browser so the account owner can approve their app.
public sealed record TikTokAuthorizationResponse(string AuthorizationUrl);
