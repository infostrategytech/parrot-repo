using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Api.Utils;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;

namespace Parrot.Api.Rest.Controllers;

[Route("api/webhooks/whatsapp")]
[ApiController]
public class WhatsAppWebhookController : ControllerBase
{
    private readonly IWhatsAppWebhookService _whatsAppWebhookService;

    public WhatsAppWebhookController(IWhatsAppWebhookService whatsAppWebhookService)
    {
        _whatsAppWebhookService = whatsAppWebhookService;
    }

    [NonAction]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult VerifyWebhook(
        [FromQuery(Name = "hub.mode")] string hubMode,
        [FromQuery(Name = "hub.verify_token")] string hubVerifyToken,
        [FromQuery(Name = "hub.challenge")] string hubChallenge)
    {
        if (!_whatsAppWebhookService.TryVerifyChallenge(hubMode, hubVerifyToken, hubChallenge, out string echoChallenge))
        {
            return Forbid();
        }

        return Content(echoChallenge, "text/plain");
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.WhatsAppWebhook)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> HandleEvent(
        [FromHeader(Name = "X-Hub-Signature-256")] string? signature,
        [FromQuery(Name = "ref")] string? identifier,
        [FromBody] WhatsAppWebhookPayload payload)
    {
        string rawBody = (string?)HttpContext.Items["RawBody"] ?? string.Empty;
        if (string.IsNullOrEmpty(signature) || !_whatsAppWebhookService.IsValidSignature(rawBody, signature))
        {
            return Unauthorized();
        }

        if (Request.Query.ContainsKey("ref"))
        {
            bool accepted = await _whatsAppWebhookService.ProcessConnectionEventAsync(
                identifier ?? string.Empty,
                payload,
                HttpContext.RequestAborted);
            return accepted ? Ok() : StatusCode(StatusCodes.Status403Forbidden);
        }

        await _whatsAppWebhookService.ProcessEventAsync(payload, HttpContext.RequestAborted);
        return Ok();
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.WhatsAppWebhook)]
    public async Task<IActionResult> VerifyConnectionWebhook(
        [FromQuery(Name = "ref")] string? identifier,
        [FromQuery(Name = "hub.mode")] string hubMode,
        [FromQuery(Name = "hub.verify_token")] string hubVerifyToken,
        [FromQuery(Name = "hub.challenge")] string hubChallenge,
        [FromServices] IWhatsAppConnectionService connections)
    {
        if (!Request.Query.ContainsKey("ref"))
        {
            return VerifyWebhook(hubMode, hubVerifyToken, hubChallenge);
        }

        if (!await connections.VerifyWebhookAsync(identifier ?? string.Empty, hubMode, hubVerifyToken))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Content(hubChallenge, "text/plain");
    }
}
