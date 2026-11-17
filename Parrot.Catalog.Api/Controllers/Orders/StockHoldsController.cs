using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;

namespace Parrot.Catalog.Api.Controllers.Orders;

/// <summary>Stock kept for Parrot's payment orders. Only a catalog:orders token, which Parrot alone mints, opens it.</summary>
[ApiController]
[Route("api/catalog/orders/holds")]
[Authorize(Policy = CatalogPolicies.Orders)]
[EnableRateLimiting(RateLimiting.Write)]
[Produces("application/json")]
public sealed class StockHoldsController : ControllerBase
{
    private readonly IStockHoldService _holds;

    public StockHoldsController(IStockHoldService holds)
    {
        _holds = holds;
    }

    [HttpPost]
    [EndpointSummary("Hold stock for a payment order until expires_at. All or nothing: a short line is a 409 saying how many are left.")]
    public async Task<ActionResult<StockHoldsResponse>> Place([FromBody] PlaceStockHoldsRequest request, CancellationToken ct)
        => Ok(await _holds.PlaceAsync(request, ct));

    [HttpPost("{reference}/commit")]
    [EndpointSummary("The order is paid: take its holds off stock. Lines whose hold ran out and lost the stock come back in short.")]
    public async Task<ActionResult<CommitStockHoldsResponse>> Commit(string reference, CancellationToken ct)
        => Ok(await _holds.CommitAsync(reference, ct));

    [HttpPost("{reference}/release")]
    [EndpointSummary("The order wasn't paid: let its holds go.")]
    public async Task<ActionResult<ReleaseStockHoldsResponse>> Release(string reference, CancellationToken ct)
        => Ok(await _holds.ReleaseAsync(reference, ct));
}
