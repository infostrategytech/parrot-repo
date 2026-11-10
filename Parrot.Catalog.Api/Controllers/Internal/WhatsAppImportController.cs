using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;

namespace Parrot.Catalog.Api.Controllers.Internal;

/// <summary>Internal endpoints consumed by the main API's WhatsApp sync worker. Requires catalog:admin scope.</summary>
[ApiController]
[Route("api/catalog/internal/whatsapp")]
[Authorize(Policy = CatalogPolicies.Admin)]
[EnableRateLimiting(RateLimiting.Write)]
[Produces("application/json")]
public sealed class WhatsAppImportController : ControllerBase
{
    private readonly IWhatsAppImportService _service;

    public WhatsAppImportController(IWhatsAppImportService service)
    {
        _service = service;
    }

    [HttpPost("sync")]
    [EndpointSummary("Bulk-upsert products from Meta. Auto-provisions catalog and product type on first call.")]
    public async Task<ActionResult<WhatsAppSyncResponse>> Sync([FromBody] WhatsAppSyncRequest request, CancellationToken ct)
    {
        WhatsAppSyncResponse result = await _service.SyncAsync(request, ct);
        return Ok(result);
    }

    [HttpPost("retire-missing")]
    [EndpointSummary("Retire published products whose SKUs were absent from Meta's latest full catalogue result.")]
    public async Task<ActionResult<WhatsAppRetireMissingResponse>> RetireMissing([FromBody] WhatsAppRetireMissingRequest request, CancellationToken ct)
    {
        WhatsAppRetireMissingResponse result = await _service.RetireMissingAsync(request, ct);
        return Ok(result);
    }

    [HttpGet("products")]
    [Authorize(Policy = CatalogPolicies.Read)]
    [EnableRateLimiting(RateLimiting.Read)]
    [EndpointSummary("Paginated product list for a WhatsApp connection.")]
    public async Task<ActionResult<PagedResponse<WhatsAppProductResponse>>> GetProducts(
        [FromQuery(Name = "connection_id")] Guid connectionId,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        PagedResponse<WhatsAppProductResponse> result = await _service.GetProductsAsync(
            new WhatsAppProductsQuery(connectionId, cursor, limit), ct);
        return Ok(result);
    }
}
