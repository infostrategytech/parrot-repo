using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;

namespace Parrot.Api.Controllers;

[ApiController]
[Route("api/auth/sso")]
[Monitor(Area = "auth.sso", SlowMs = 3000)] // round trips to Google
public class SsoController : ControllerBase
{
    private const string ExternalCookieScheme = "ExternalCookies";

    private readonly IAuthService _authService;
    private readonly FrontendSettings _frontendSettings;

    public SsoController(IAuthService authService, IOptions<FrontendSettings> frontendSettings)
    {
        _authService = authService;
        _frontendSettings = frontendSettings.Value;
    }

    [HttpGet("google")]
    [EndpointSummary("Start Google sign-in")]
    [EndpointDescription("Challenges the browser over to Google's consent screen. Nobody is signed in yet; the session is made at the callback that follows.")]
    public IActionResult InitiateGoogle()
    {
        AuthenticationProperties properties = new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(GoogleCallback), "Sso"),
        };
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    [HttpGet("google/callback")]
    [EndpointSummary("Finish Google sign-in")]
    [EndpointDescription("Called by Google. Creates the account on a first sign-in, then redirects to the frontend at /sso-callback with the token and the profile as query parameters — business name and logo are still empty until setup has run. A cancelled exchange or one missing an email address or Google id lands on /login with error=sso_failed or error=sso_missing_claims.")]
    public async Task<IActionResult> GoogleCallback()
    {
        AuthenticateResult result = await HttpContext.AuthenticateAsync(ExternalCookieScheme);

        if (!result.Succeeded || result.Principal is null)
        {
            return Redirect($"{_frontendSettings.BaseUrl}/login?error=sso_failed");
        }

        string? email = result.Principal.FindFirstValue(ClaimTypes.Email);
        string? googleId = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(googleId))
        {
            return Redirect($"{_frontendSettings.BaseUrl}/login?error=sso_missing_claims");
        }

        await HttpContext.SignOutAsync(ExternalCookieScheme);

        AuthResponse authResponse = await _authService.ExternalLoginAsync(email, "Google", googleId);

        // Business name and logo are filled in by the setup step after sign-in,
        // so both are optional here and the callback must not assume them.
        string redirectUrl = string.Format(
            "{0}/sso-callback?token={1}&expiresAt={2}&userId={3}&email={4}&businessName={5}&logoUrl={6}&phoneNumber={7}",
            _frontendSettings.BaseUrl,
            Uri.EscapeDataString(authResponse.Token),
            Uri.EscapeDataString(authResponse.ExpiresAt.ToString("O")),
            Uri.EscapeDataString(authResponse.UserId),
            Uri.EscapeDataString(authResponse.Email),
            Uri.EscapeDataString(authResponse.BusinessName ?? string.Empty),
            Uri.EscapeDataString(authResponse.LogoUrl ?? string.Empty),
            Uri.EscapeDataString(authResponse.PhoneNumber ?? string.Empty));

        return Redirect(redirectUrl);
    }
}
