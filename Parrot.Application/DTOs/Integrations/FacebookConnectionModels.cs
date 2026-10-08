using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Integrations;

// Everything the Channels setup screen needs to render the current step.
public sealed record FacebookSetupResponse(
    Guid ConnectionId,
    string WebhookUrl,
    string VerifyToken,
    bool WebhookVerified,
    string? PageId,
    string? PageName,
    bool HasAccessToken,
    // A manual connection's own App Secret is saved; its webhooks are checked with it.
    bool HasAppSecret);

// Page details the customer pastes in after Meta verifies the webhook.
public sealed class FacebookPageRequest
{
    [Required, RegularExpression("^[0-9]{1,64}$")]
    public required string PageId { get; set; }

    [Required, StringLength(128, MinimumLength = 1)]
    public required string PageName { get; set; }

    [Required, StringLength(8192, MinimumLength = 1)]
    public required string AccessToken { get; set; }
}
