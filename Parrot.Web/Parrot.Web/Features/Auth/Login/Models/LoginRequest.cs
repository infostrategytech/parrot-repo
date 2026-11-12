namespace Parrot.Web.Features.Auth.Login.Models;

public class LoginRequest
{
    // The API takes an email address or a phone number here. This screen only
    // offers email for now, so it is always an email address.
    public string Identifier { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    // "Don't ask again on this device": the token an earlier two-factor sign-in gave this browser, so the code isn't
    // asked for again (settings-backend-requests). Left out of the request when there is none.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RememberedDevice { get; set; }
}
