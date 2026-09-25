using System.ComponentModel.DataAnnotations;
using Parrot.Application.Auth;

namespace Parrot.Application.DTOs.Auth;

public record ResetPasswordRequest
{
    [Required(ErrorMessage = "User ID is required")]
    public required string UserId { get; init; }

    [Required(ErrorMessage = "Token is required")]
    public required string Token { get; init; }

    [Required(ErrorMessage = "New password is required")]
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength, ErrorMessage = "Password must be between 12 and 128 characters")]
    public required string NewPassword { get; init; }

    [Required(ErrorMessage = "Confirm password is required")]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match")]
    public required string ConfirmNewPassword { get; init; }
}
