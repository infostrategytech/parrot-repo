using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Api.Utils;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;

namespace Parrot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Monitor(Area = "auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [Monitor("auth.register", SlowMs = 3000)] // password hashing + breached-password lookup
    [HttpPost("register")]
    [EndpointSummary("Sign up with an email address and a password")]
    [EndpointDescription("Email and password only — no phone number is collected at sign-up. With email verification on, the default, this answers 201 with no session and emails a six-digit code that POST /api/Auth/verify-email turns into one; with verification off it answers 200 already signed in. An address already in use is a 409.")]
    [EnableRateLimiting(RateLimitPolicies.Register)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RegisterAsync([FromBody] RegisterRequest request)
    {
        AuthResponse? response = await _authService.RegisterAsync(request);

        // 200 with a session when verification is switched off; 201 with
        // nothing when a code is on its way.
        return response is null ? StatusCode(StatusCodes.Status201Created) : Ok(response);
    }

    [Monitor("auth.login", SlowMs = 1500)] // password hashing by design
    [HttpPost("login")]
    [EndpointSummary("Sign in with an email address or a phone number")]
    [EndpointDescription("Accepts either credential in the same field. Returns a session token plus the profile as it stands; a token is also what the profile endpoints expect as a Bearer header.")]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthResponse>> LoginAsync([FromBody] LoginRequest request)
    {
        AuthResponse response = await _authService.LoginAsync(request);
        return Ok(response);
    }

    [HttpPost("verify-email")]
    [EndpointSummary("Turn the emailed code into a session")]
    [EndpointDescription("The second half of sign-up. Six digits, spaces between groups tolerated. The answer is the same session a sign-in would have given; a wrong, expired or already-used code is a 401.")]
    [EnableRateLimiting(RateLimitPolicies.VerifyEmail)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthResponse>> VerifyEmailAsync([FromBody] VerifyEmailRequest request)
    {
        AuthResponse response = await _authService.VerifyEmailAsync(request);
        return Ok(response);
    }

    [HttpPost("resend-verification")]
    [EndpointSummary("Ask for another verification code")]
    [EndpointDescription("Sends another code to the address. Answers 204 whether or not the address exists or is already confirmed, so it cannot be used to find out which addresses are registered.")]
    [EnableRateLimiting(RateLimitPolicies.ResendVerification)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ResendVerificationEmailAsync([FromBody] ResendVerificationEmailRequest request)
    {
        await _authService.ResendVerificationEmailAsync(request);
        return NoContent();
    }

    [HttpPost("forgot-password")]
    [EndpointSummary("Start a password reset")]
    [EndpointDescription("Emails a link carrying the userId and a reset token. Answers 204 whether or not the address is registered, for the same reason as resend-verification: not answering would be an answer.")]
    [EnableRateLimiting(RateLimitPolicies.ForgotPassword)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ForgotPasswordAsync([FromBody] ForgotPasswordRequest request)
    {
        await _authService.ForgotPasswordAsync(request);
        return NoContent();
    }

    [HttpPost("reset-password")]
    [EndpointSummary("Complete a password reset")]
    [EndpointDescription("Takes the userId and token from the emailed link along with the new password. An unknown userId is a 404, an expired or used token and a password that fails the policy are both 400s. The token cannot be used twice.")]
    [EnableRateLimiting(RateLimitPolicies.ResetPassword)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ResetPasswordAsync([FromBody] ResetPasswordRequest request)
    {
        await _authService.ResetPasswordAsync(request);
        return NoContent();
    }

    [Authorize]
    [AllowWithoutWorkspace]
    [HttpPost("change-password")]
    [EndpointSummary("Change the password of the signed-in account")]
    [EndpointDescription("Needs a session. The current password has to match and the new one has to satisfy the password policy: 8 to 128 characters. A missing session is a 401, an account that has since gone is a 404.")]
    [EnableRateLimiting(RateLimitPolicies.ChangePassword)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordRequest request)
    {
        string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");

        if (userId is null)
        {
            return Unauthorized();
        }

        await _authService.ChangePasswordAsync(userId, request);
        return NoContent();
    }
}
