using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;

namespace Parrot.Catalog.Api.Controllers.Admin;

[ApiController]
[Route("api/catalog/admin/offers")]
[Authorize(Policy = CatalogPolicies.Write)]
[EnableRateLimiting(RateLimiting.Write)]
[Produces("application/json")]
public sealed class OffersController : ControllerBase
{
    private readonly IOfferAdminService _service;

    public OffersController(IOfferAdminService service)
    {
        _service = service;
    }

    [HttpPost]
    [EndpointSummary("Create an offer on a product or a variant (Starter/Pro/Enterprise). Prices are added separately.")]
    public async Task<ActionResult<OfferAdminResponse>> Create([FromBody] CreateOfferRequest request, CancellationToken ct)
    {
        OfferAdminResponse created = await _service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OfferAdminResponse>> Get(Guid id, CancellationToken ct) => Ok(await _service.GetAsync(id, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<OfferAdminResponse>> Update(Guid id, [FromBody] UpdateOfferRequest request, CancellationToken ct) => Ok(await _service.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/prices")]
    [EndpointSummary("Add a price. Active prices for the same currency and price type may not overlap in time (409).")]
    public async Task<ActionResult<PriceResponse>> AddPrice(Guid id, [FromBody] CreatePriceRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _service.AddPriceAsync(id, request, ct));

    [HttpPut("{id:guid}/prices/{priceId:guid}")]
    public async Task<ActionResult<PriceResponse>> UpdatePrice(Guid id, Guid priceId, [FromBody] UpdatePriceRequest request, CancellationToken ct)
        => Ok(await _service.UpdatePriceAsync(id, priceId, request, ct));

    [HttpDelete("{id:guid}/prices/{priceId:guid}")]
    public async Task<IActionResult> DeletePrice(Guid id, Guid priceId, CancellationToken ct)
    {
        await _service.DeletePriceAsync(id, priceId, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/catalog/admin/availability")]
[Authorize(Policy = CatalogPolicies.Write)]
[EnableRateLimiting(RateLimiting.Write)]
[Produces("application/json")]
public sealed class AvailabilityController : ControllerBase
{
    private readonly IAvailabilityAdminService _service;

    public AvailabilityController(IAvailabilityAdminService service)
    {
        _service = service;
    }

    [HttpPost]
    public async Task<ActionResult<AvailabilityResponse>> Create([FromBody] CreateAvailabilityRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _service.CreateAsync(request, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AvailabilityResponse>> Update(Guid id, [FromBody] UpdateAvailabilityRequest request, CancellationToken ct) => Ok(await _service.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/catalog/admin/relationships")]
[Authorize(Policy = CatalogPolicies.Write)]
[EnableRateLimiting(RateLimiting.Write)]
[Produces("application/json")]
public sealed class RelationshipsController : ControllerBase
{
    private readonly IRelationshipAdminService _service;

    public RelationshipsController(IRelationshipAdminService service)
    {
        _service = service;
    }

    [HttpPost]
    [EndpointSummary("Link two products (bundled_with, requires, compatible_with, alternative_to, upgrade_to, downgrade_to, upsell, cross_sell, replaces).")]
    public async Task<ActionResult<RelationshipResponse>> Create([FromBody] CreateRelationshipRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _service.CreateAsync(request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/catalog/admin/media")]
[Authorize(Policy = CatalogPolicies.Write)]
[EnableRateLimiting(RateLimiting.Write)]
[Produces("application/json")]
public sealed class MediaController : ControllerBase
{
    private readonly IMediaAdminService _service;

    public MediaController(IMediaAdminService service)
    {
        _service = service;
    }

    [HttpPost]
    [EndpointSummary("Record an uploaded object (image, video, document). The S3 key must sit under the tenant's prefix; no bytes pass through this API.")]
    public async Task<ActionResult<MediaResponse>> Create([FromBody] CreateMediaRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _service.CreateAsync(request, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<MediaResponse>> Update(Guid id, [FromBody] UpdateMediaRequest request, CancellationToken ct) => Ok(await _service.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
