using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Application.Payments;

namespace Parrot.Api.Controllers;

/// <summary>
/// Where Accelerate says a payment link was paid. It carries no token: what proves it came from
/// Accelerate is its hash, an HMAC-SHA512 of the event id with the platform's client secret,
/// checked before anything else. Accelerate expects an answer within five seconds and resends
/// what isn't answered 200; a resend is recorded once.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/accelerate")]
[Monitor(Area = "webhooks.accelerate", SlowMs = 2000)]
public sealed class AccelerateWebhookController(PaymentConfirmationService payments) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Receive([FromBody] AccelerateWebhook webhook, [FromHeader(Name = "hash")] string? hash, CancellationToken ct)
    {
        WebhookOutcome outcome = await payments.HandleAsync(webhook, hash, ct);
        return outcome switch
        {
            WebhookOutcome.BadSignature => Unauthorized(new { error = "Invalid signature" }),
            WebhookOutcome.TryAgain => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Try again shortly" }),
            WebhookOutcome.Duplicate => Ok(new { received = true, duplicate = true }),
            _ => Ok(new { received = true }),
        };
    }
}
