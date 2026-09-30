using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Auth;

/// <summary>The address a reset link should be sent to.</summary>
[Description("Starts a password reset. Nothing here reveals whether the address exists.")]
public record ForgotPasswordRequest
{
    [Description("The address to send the reset link to, if one is registered.")]
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email address")]
    public required string Email { get; init; }
}
