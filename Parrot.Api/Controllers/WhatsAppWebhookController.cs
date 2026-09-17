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
    public async Task<IActionResult> HandleEvent([FromBody] WhatsAppWebhookPayload payload)
    {
        // Per-connection ingestion must validate the applicable Meta App Secret before publishing.
        if (Request.Query.ContainsKey("ref"))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await _whatsAppWebhookService.ProcessEventAsync(payload);
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
