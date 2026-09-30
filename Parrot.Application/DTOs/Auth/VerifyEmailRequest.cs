using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Auth;

/// <summary>The code emailed at sign-up, and the address it went to.</summary>
[Description("Exchanges the emailed verification code for a session.")]
public record VerifyEmailRequest
{
    [Description("The address the code was sent to.")]
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email address")]
    public required string Email { get; init; }

    // Six digits; spaces between groups are tolerated and stripped by the service.
    [Description("Six digits; spaces between groups are tolerated and stripped.")]
    [Required(ErrorMessage = "Verification code is required")]
    [StringLength(8, MinimumLength = 6, ErrorMessage = "Enter the 6-digit code")]
    public required string Code { get; init; }
}
