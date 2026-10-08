using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Api.Monitoring;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Rest.Controllers;

[ApiController]
[Authorize]
[Route("api/tiktok/setup")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
// Authenticated setup endpoints for TikTok, plus the OAuth callback TikTok redirects the browser to.
[Monitor(Area = "tiktok.setup", SlowMs = 3000)] // round trips to TikTok
public sealed class TikTokSetupController(
    ITikTokConnectionService connections,
    IOptions<FrontendSettings> frontend,
    ILogger<TikTokSetupController> logger) : ControllerBase
{
    // GET /api/tiktok/setup - the current step and the redirect URI to add to the TikTok app.
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        string? owner = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(owner)) return Unauthorized();
        return Ok(await connections.GetSetupAsync(owner, cancellationToken));
    }

    // PUT /api/tiktok/setup/app - saves the customer's TikTok app and registers its webhook with TikTok.
    [HttpPut("app")]
    public async Task<IActionResult> SaveApp([FromBody] TikTokAppRequest request, CancellationToken cancellationToken)
    {
        string? owner = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(owner)) return Unauthorized();
        await connections.SaveAppAsync(owner, request, cancellationToken);
        return NoContent();
    }

    // GET /api/tiktok/setup/authorize - the TikTok consent URL to send the browser to.
    [HttpGet("authorize")]
    public async Task<IActionResult> Authorize(CancellationToken cancellationToken)
    {
        string? owner = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(owner)) return Unauthorized();
        return Ok(await connections.GetAuthorizationAsync(owner, cancellationToken));
    }

    // GET /api/tiktok/setup/callback - TikTok sends the browser here once the owner approves or cancels.
    // No session comes with it; the sealed state names the connection. Always ends on the Channels page:
    // ?tiktok=connected, ?tiktok=cancelled, or ?tiktok=failed with a reason to show.
    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state,
        [FromQuery] string? error, CancellationToken cancellationToken)
    {
        string channels = $"{frontend.Value.BaseUrl.TrimEnd('/')}/dashboard/channels";
        if (!string.IsNullOrEmpty(error)) return Redirect($"{channels}?tiktok=cancelled");

        try
        {
            await connections.CompleteAuthorizationAsync(state, code, cancellationToken);
        }
        catch (DomainException ex)
        {
            logger.LogWarning("TikTok authorization failed: {Message}", ex.Message);
            return Redirect($"{channels}?tiktok=failed&reason={Uri.EscapeDataString(ex.Message)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "TikTok authorization failed unexpectedly.");
            return Redirect($"{channels}?tiktok=failed&reason={Uri.EscapeDataString("Something went wrong connecting TikTok. Try again.")}");
        }

        return Redirect($"{channels}?tiktok=connected");
    }

    // POST /api/tiktok/setup/disable - disconnects the account; its webhooks are refused from now on.
    [HttpPost("disable")]
    public async Task<IActionResult> Disable(CancellationToken cancellationToken)
    {
        string? owner = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(owner)) return Unauthorized();
        await connections.DisableAsync(owner, cancellationToken);
        return NoContent();
    }
}
