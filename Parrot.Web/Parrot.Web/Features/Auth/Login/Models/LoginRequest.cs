namespace Parrot.Web.Features.Auth.Login.Models;

public class LoginRequest
{
    // The API takes an email address or a phone number here. This screen only
    // offers email for now, so it is always an email address.
    public string Identifier { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
