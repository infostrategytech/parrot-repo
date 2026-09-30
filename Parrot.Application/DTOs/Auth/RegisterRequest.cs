using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Parrot.Application.Auth;

namespace Parrot.Application.DTOs.Auth;

/// <summary>Email and password — no phone number is collected at sign-up.</summary>
[Description("Creates an account from an email address and a password.")]
public record RegisterRequest
{
    [Description("The address the account is created under, and where the verification code is sent.")]
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email address")]
    public required string Email { get; init; }

    [Description("Between 8 and 128 characters.")]
    [Required(ErrorMessage = "Password is required")]
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength, ErrorMessage = "Password must be between 8 and 128 characters")]
    public required string Password { get; init; }

    [Description("Has to match password; a mismatch is a 400 before anything is created.")]
    [Required(ErrorMessage = "Confirm password is required")]
    [Compare(nameof(Password), ErrorMessage = "Passwords do not match")]
    public required string ConfirmPassword { get; init; }
}
