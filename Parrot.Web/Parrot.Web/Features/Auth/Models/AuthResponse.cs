namespace Parrot.Web.Features.Auth.Models;

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    // Null until the workspace welcome card has saved one.
    public string? BusinessName { get; set; }

    // The business logo, or null when none has been uploaded.
    public string? LogoUrl { get; set; }
}
