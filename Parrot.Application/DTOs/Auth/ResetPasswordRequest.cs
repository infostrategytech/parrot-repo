using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Parrot.Application.Auth;

namespace Parrot.Application.DTOs.Auth;

/// <summary>What the link in the reset email carries, plus the password being set.</summary>
[Description("Completes a password reset using the token from the emailed link.")]
public record ResetPasswordRequest
{
    [Description("The account's id, as carried on the link in the email.")]
    [Required(ErrorMessage = "User ID is required")]
    public required string UserId { get; init; }

    [Description("The reset token carried on the link in the email.")]
    [Required(ErrorMessage = "Token is required")]
    public required string Token { get; init; }

    [Description("Between 8 and 128 characters.")]
    [Required(ErrorMessage = "New password is required")]
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength, ErrorMessage = "Password must be between 8 and 128 characters")]
    public required string NewPassword { get; init; }

    [Description("Has to match newPassword.")]
    [Required(ErrorMessage = "Confirm password is required")]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match")]
    public required string ConfirmNewPassword { get; init; }
}
