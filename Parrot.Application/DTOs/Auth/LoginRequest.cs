using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Auth;

/// <summary>One field, so a person never has to say which credential they are typing.</summary>
[Description("Credentials for signing in. The server works out from identifier whether it is holding an email address or a phone number.")]
public record LoginRequest
{
    // An email address or a phone number. Kept as one field so a person never
    // has to say which they are typing; the service works out which it is.
    [Description("An email address or a phone number, in any of the shapes a person would type them. The server works out which it is.")]
    [Required(ErrorMessage = "Enter your email or phone number")]
    required public string Identifier { get; init; }

    [Description("The password of the account being signed into.")]
    [Required(ErrorMessage = "Password is required")]
    required public string Password { get; init; }
}
