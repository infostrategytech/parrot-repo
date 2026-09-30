using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Auth;

/// <summary>The address to send a fresh verification code to.</summary>
[Description("Asks for another verification code. The answer does not depend on whether the address is registered.")]
public record ResendVerificationEmailRequest
{
    [Description("The address to send another code to.")]
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email address")]
    public required string Email { get; init; }
}
