using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Integrations;

public sealed record FacebookSetupResponse(
    Guid ConnectionId,
    string WebhookUrl,
    string VerifyToken,
    bool WebhookVerified,
    string? PageId,
    string? PageName,
    bool HasAccessToken);

public sealed class FacebookPageRequest
{
    [Required, RegularExpression("^[0-9]{1,64}$")]
    public required string PageId { get; set; }

    [Required, StringLength(128, MinimumLength = 1)]
    public required string PageName { get; set; }

    [Required, StringLength(8192, MinimumLength = 1)]
    public required string AccessToken { get; set; }
}
