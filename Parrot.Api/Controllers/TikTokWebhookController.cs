using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Api.Monitoring;
using Parrot.Api.Utils;
using Parrot.Application.Integrations;

namespace Parrot.Api.Rest.Controllers;

[Route("api/webhooks/tiktok")]
[ApiController]
[Monitor(Area = "webhooks.tiktok", SlowMs = 500)] // TikTok expects a quick acknowledgement
public class TikTokWebhookController(ITikTokWebhookService webhooks) : ControllerBase
{
    // TikTok posts a connection's events to the URL Parrot registered for it, which ends in that connection's
    // reference. TikTok-Signature is checked, with the connection's own App Secret, before anything is read.
    // There is no GET challenge: TikTok never verifies the URL.
    [HttpPost("{identifier}")]
    [EnableRateLimiting(RateLimitPolicies.TikTokWebhook)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> HandleEvent(
        string identifier,
        [FromHeader(Name = "TikTok-Signature")] string? signature)
    {
        string rawBody = (string?)HttpContext.Items["RawBody"] ?? string.Empty;

        if (!await webhooks.IsAuthenticAsync(identifier, rawBody, signature, HttpContext.RequestAborted))
        {
            return Unauthorized();
        }

        return await webhooks.ProcessEventAsync(identifier, rawBody, HttpContext.RequestAborted)
            ? Ok()
            : StatusCode(StatusCodes.Status403Forbidden);
    }
}
