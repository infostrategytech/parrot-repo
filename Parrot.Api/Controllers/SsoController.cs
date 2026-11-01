using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;

namespace Parrot.Api.Controllers;

[ApiController]
[Route("api/auth/sso")]
[Monitor(Area = "auth.sso", SlowMs = 3000)] // round trips to Google or Facebook
public class SsoController : ControllerBase
{
    private const string ExternalCookieScheme = "ExternalCookies";

    private readonly IAuthService _authService;
    private readonly FrontendSettings _frontendSettings;
    private readonly IAuthenticationSchemeProvider _schemes;

    public SsoController(IAuthService authService, IOptions<FrontendSettings> frontendSettings, IAuthenticationSchemeProvider schemes)
    {
        _authService = authService;
        _frontendSettings = frontendSettings.Value;
        _schemes = schemes;
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
    public Task<IActionResult> GoogleCallback() =>
        FinishAsync("Google", failed: "sso_failed", missing: "sso_missing_claims");

    [HttpGet("facebook")]
    [EndpointSummary("Start Facebook sign-in")]
    [EndpointDescription("Sends the browser to Facebook Login, asking for the person's name and email. Nobody is signed in yet; the session is made at the callback that follows. While no Facebook app is configured (FacebookOAuth:AppId and AppSecret), it lands on /login with error=facebook_unavailable.")]
    public async Task<IActionResult> InitiateFacebook()
    {
        if (await _schemes.GetSchemeAsync(FacebookDefaults.AuthenticationScheme) is null)
            return Redirect($"{_frontendSettings.BaseUrl}/login?error=facebook_unavailable");

        AuthenticationProperties properties = new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(FacebookCallback), "Sso"),
        };
        return Challenge(properties, FacebookDefaults.AuthenticationScheme);
    }

    [HttpGet("facebook/callback")]
    [EndpointSummary("Finish Facebook sign-in")]
    [EndpointDescription("Called after Facebook Login. As for Google: creates the account on a first sign-in, then redirects to /sso-callback. A cancelled exchange lands on /login with error=facebook_failed; a Facebook account with no email address (one registered by phone, or with email declined) with error=facebook_no_email.")]
    public Task<IActionResult> FacebookCallback() =>
        FinishAsync("Facebook", failed: "facebook_failed", missing: "facebook_no_email");

    // Both providers end the same way: the external sign-in is read from its short-lived cookie, the account is
    // found or created by email, and the frontend is handed the session.
    private async Task<IActionResult> FinishAsync(string provider, string failed, string missing)
    {
        AuthenticateResult result = await HttpContext.AuthenticateAsync(ExternalCookieScheme);

        if (!result.Succeeded || result.Principal is null)
        {
            return Redirect($"{_frontendSettings.BaseUrl}/login?error={failed}");
        }

        string? email = result.Principal.FindFirstValue(ClaimTypes.Email);
        string? providerKey = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(providerKey))
        {
            await HttpContext.SignOutAsync(ExternalCookieScheme);
            return Redirect($"{_frontendSettings.BaseUrl}/login?error={missing}");
        }

        await HttpContext.SignOutAsync(ExternalCookieScheme);

        AuthResponse authResponse = await _authService.ExternalLoginAsync(email, provider, providerKey);
        // Business name and logo are filled in by the setup step after sign-in,
        // so both are optional here and the callback must not assume them.
        string redirectUrl = string.Format(
            "{0}/sso-callback?token={1}&expiresAt={2}&userId={3}&email={4}&businessName={5}&logoUrl={6}&phoneNumber={7}&workspaceId={8}&role={9}&permissions={10}",
            _frontendSettings.BaseUrl,
            Uri.EscapeDataString(authResponse.Token),
            Uri.EscapeDataString(authResponse.ExpiresAt.ToString("O")),
            Uri.EscapeDataString(authResponse.UserId),
            Uri.EscapeDataString(authResponse.Email),
            Uri.EscapeDataString(authResponse.BusinessName ?? string.Empty),
            Uri.EscapeDataString(authResponse.LogoUrl ?? string.Empty),
            Uri.EscapeDataString(authResponse.PhoneNumber ?? string.Empty),
            Uri.EscapeDataString(authResponse.WorkspaceId),
            Uri.EscapeDataString(authResponse.Role),
            Uri.EscapeDataString(string.Join(',', authResponse.Permissions)));

        return Redirect(redirectUrl);
    }
}
