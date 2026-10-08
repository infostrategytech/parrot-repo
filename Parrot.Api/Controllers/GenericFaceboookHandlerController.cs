using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Api.Utils;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Api.Monitoring;

namespace Parrot.Api.Rest.Controllers;

[Route("api/webhooks/facebook")]
[ApiController]
[Monitor(Area = "webhooks.facebook", SlowMs = 500)] // Meta expects a quick acknowledgement
public class FacebookWebhookController : ControllerBase
{
    private readonly IFacebookWebhookService _facebookWebhookService;
    private readonly IFacebookConnectionService _connections;

    public FacebookWebhookController(IFacebookWebhookService facebookWebhookService, IFacebookConnectionService connections)
    {
        _facebookWebhookService = facebookWebhookService;
        _connections = connections;
    }

    // Meta calls this GET when the customer subscribes their Page to Parrot. The ?ref= value names the
    // connection whose verify token must match, so one customer's token cannot verify another's callback.
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.FacebookWebhook)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> VerifyWebhook(
        [FromQuery(Name = "ref")] string? identifier,
        [FromQuery(Name = "hub.mode")] string hubMode,
        [FromQuery(Name = "hub.verify_token")] string hubVerifyToken,
        [FromQuery(Name = "hub.challenge")] string hubChallenge)
    {
        if (!await _connections.VerifyWebhookAsync(identifier ?? string.Empty, hubMode, hubVerifyToken, HttpContext.RequestAborted))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Content(hubChallenge, "text/plain");
    }

    // Meta posts all page events here — messages, reactions, deliveries, postbacks, page changes.
    // X-Hub-Signature-256 is checked before any processing, with the connection's own App Secret
    // (manual setup) or Parrot's (Embedded Signup); Facebook:SignatureMode decides what a failure does.
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.FacebookWebhook)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> HandleEvent(
        [FromHeader(Name = "X-Hub-Signature-256")] string? signature,
        [FromQuery(Name = "ref")] string? identifier,
        [FromBody] FacebookWebhookPayload payload)
    {
        string rawBody = (string?)HttpContext.Items["RawBody"] ?? string.Empty;

        if (!await _facebookWebhookService.IsAuthenticAsync(identifier, rawBody, signature, HttpContext.RequestAborted))
        {
            return Unauthorized();
        }

        bool accepted = await _facebookWebhookService.ProcessConnectionEventAsync(
            identifier ?? string.Empty, payload, rawBody, HttpContext.RequestAborted);
        return accepted ? Ok() : StatusCode(StatusCodes.Status403Forbidden);
    }
}
