using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Api.Monitoring;
using Parrot.Api.Utils;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;

namespace Parrot.Api.Rest.Controllers;

// Instagram DMs for manually connected accounts. Each connection's callback URL carries its own ?ref=.
[Route("api/webhooks/instagram")]
[ApiController]
[Monitor(Area = "webhooks.instagram", SlowMs = 500)] // Meta expects a quick acknowledgement
public sealed class InstagramWebhookController(IInstagramWebhookService webhooks, IInstagramConnectionService connections) : ControllerBase
{
    // Meta calls this GET when the business saves the callback URL. The ?ref= names the connection
    // whose verify token must match, so one business's token cannot verify another's callback.
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.InstagramWebhook)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> VerifyWebhook(
        [FromQuery(Name = "ref")] string? identifier,
        [FromQuery(Name = "hub.mode")] string hubMode,
        [FromQuery(Name = "hub.verify_token")] string hubVerifyToken,
        [FromQuery(Name = "hub.challenge")] string hubChallenge)
    {
        if (!await connections.VerifyWebhookAsync(identifier ?? string.Empty, hubMode, hubVerifyToken, HttpContext.RequestAborted))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Content(hubChallenge, "text/plain");
    }

    // X-Hub-Signature-256 is checked first, with the connection's Instagram app secret;
    // Instagram:SignatureMode decides what a failure does.
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.InstagramWebhook)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> HandleEvent(
        [FromHeader(Name = "X-Hub-Signature-256")] string? signature,
        [FromQuery(Name = "ref")] string? identifier,
        [FromBody] FacebookWebhookPayload payload)
    {
        string rawBody = (string?)HttpContext.Items["RawBody"] ?? string.Empty;

        if (!await webhooks.IsAuthenticAsync(identifier, rawBody, signature, HttpContext.RequestAborted))
        {
            return Unauthorized();
        }

        bool accepted = await webhooks.ProcessConnectionEventAsync(identifier ?? string.Empty, payload, rawBody, HttpContext.RequestAborted);
        return accepted ? Ok() : StatusCode(StatusCodes.Status403Forbidden);
    }
}
