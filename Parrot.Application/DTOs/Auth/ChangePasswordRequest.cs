using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Parrot.Application.Auth;

namespace Parrot.Application.DTOs.Auth;

/// <summary>Needs a session — it changes the password of whoever is holding the token.</summary>
[Description("Changes the password of the signed-in account.")]
public record ChangePasswordRequest
{
    [Description("The password in use now.")]
    [Required(ErrorMessage = "Current password is required")]
    public required string CurrentPassword { get; init; }

    [Description("Between 8 and 128 characters.")]
    [Required(ErrorMessage = "New password is required")]
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength, ErrorMessage = "Password must be between 8 and 128 characters")]
    public required string NewPassword { get; init; }

    [Description("Has to match newPassword.")]
    [Required(ErrorMessage = "Confirm password is required")]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match")]
    public required string ConfirmNewPassword { get; init; }
}
