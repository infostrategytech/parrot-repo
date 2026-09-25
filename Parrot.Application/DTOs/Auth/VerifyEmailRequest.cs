using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Auth;

public record VerifyEmailRequest
{
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email address")]
    public required string Email { get; init; }

    // Six digits; spaces between groups are tolerated and stripped by the service.
    [Required(ErrorMessage = "Verification code is required")]
    [StringLength(8, MinimumLength = 6, ErrorMessage = "Enter the 6-digit code")]
    public required string Code { get; init; }
}
