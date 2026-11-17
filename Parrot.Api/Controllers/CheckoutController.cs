using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;
using Parrot.Api.Utils;
using Parrot.Application.Payments;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// The customer's checkout page. Public: the customer opens it from a chat, signed out. The
/// token in the address is the only key, and it names one order in one workspace; prices are
/// always read live, never taken from the address or the request.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/checkout")]
[EnableRateLimiting(RateLimitPolicies.Checkout)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "checkout", SlowMs = 3000)]
public sealed class CheckoutController(CheckoutService checkouts) : ControllerBase
{
    [HttpGet("{token}")]
    [EndpointSummary("A checkout, priced live")]
    [EndpointDescription("The business, the items with the agent's quote and today's price, the total Pay will charge, and whether it can be paid now (canPay, with message saying why not). State: open, paying (paymentUrl to finish), paid, expired, replaced or unavailable.")]
    [ProducesResponseType(typeof(CheckoutView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CheckoutView>> Get(string token, CancellationToken ct)
    {
        try
        {
            return Ok(await checkouts.GetAsync(token, ct));
        }
        catch (CheckoutNotFoundException ex)
        {
            throw new NotFoundException(ex.Message);
        }
    }

    [HttpPost("{token}/pay")]
    [EndpointSummary("Press Pay: make the payment link")]
    [EndpointDescription("Send the total the page showed (expectedTotal). If anything changed since (a price, an item sold out), the answer is 409 with the checkout as it is now, so the page can show it and ask again. Pressing Pay again while the payment link is open returns the same link.")]
    [ProducesResponseType(typeof(PayCheckoutResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CheckoutChangedResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Pay(string token, [FromBody] PayCheckoutRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await checkouts.PayAsync(token, request, ct));
        }
        catch (CheckoutNotFoundException ex)
        {
            throw new NotFoundException(ex.Message);
        }
        catch (CheckoutChangedException ex)
        {
            return Conflict(new CheckoutChangedResponse("CHECKOUT_CHANGED", ex.Message, ex.View));
        }
        catch (CheckoutRefusedException ex)
        {
            return Conflict(new CheckoutChangedResponse("CHECKOUT_REFUSED", ex.Message, await checkouts.GetAsync(token, ct)));
        }
        catch (CheckoutUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponse { ErrorCode = "CHECKOUT_UNAVAILABLE", Message = ex.Message });
        }
    }
}

/// <summary>Pay refused because the checkout changed: why, and the checkout as it is now.</summary>
public sealed record CheckoutChangedResponse(string ErrorCode, string Message, CheckoutView Checkout);
