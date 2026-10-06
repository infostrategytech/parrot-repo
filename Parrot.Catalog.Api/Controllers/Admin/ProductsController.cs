using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Api.Controllers.Admin;

[ApiController]
[Route("api/catalog/admin/products")]
[Authorize(Policy = CatalogPolicies.Write)]
[EnableRateLimiting(RateLimiting.Write)]
[Produces("application/json")]
public sealed class ProductsController : ControllerBase
{
    private readonly IProductAdminService _products;
    private readonly IOfferAdminService _offers;
    private readonly IAvailabilityAdminService _availability;
    private readonly IRelationshipAdminService _relationships;
    private readonly IMediaAdminService _media;

    public ProductsController(IProductAdminService products, IOfferAdminService offers, IAvailabilityAdminService availability, IRelationshipAdminService relationships, IMediaAdminService media)
    {
        _products = products;
        _offers = offers;
        _availability = availability;
        _relationships = relationships;
        _media = media;
    }

    [HttpPost]
    [EndpointSummary("Create a draft product. Attributes are validated against the type's current version, which the product then pins.")]
    public async Task<ActionResult<ProductAdminResponse>> Create([FromBody] CreateProductRequest request, CancellationToken ct)
    {
        ProductAdminResponse created = await _products.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<ProductAdminResponse>>> List(
        [FromQuery(Name = "catalog_id")] Guid? catalogId,
        [FromQuery] ProductStatus? status,
        [FromQuery(Name = "product_type_id")] Guid? productTypeId,
        [FromQuery(Name = "category_id")] Guid? categoryId,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken ct)
        => Ok(await _products.ListAsync(new ProductListQuery(catalogId, status, productTypeId, categoryId, cursor, limit), ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProductAdminResponse>> Get(Guid id, CancellationToken ct) => Ok(await _products.GetAsync(id, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProductAdminResponse>> Update(Guid id, [FromBody] UpdateProductRequest request, CancellationToken ct) => Ok(await _products.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _products.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/publish")]
    [Authorize(Policy = CatalogPolicies.Publish)]
    [EndpointSummary("Publish in one transaction: version snapshot, derived index, canonical document, ProductPublished event.")]
    public async Task<ActionResult<ProductAdminResponse>> Publish(Guid id, CancellationToken ct) => Ok(await _products.PublishAsync(id, ct));

    [HttpPost("{id:guid}/retire")]
    [Authorize(Policy = CatalogPolicies.Publish)]
    public async Task<ActionResult<ProductAdminResponse>> Retire(Guid id, CancellationToken ct) => Ok(await _products.RetireAsync(id, ct));

    // ---- variants ---------------------------------------------------------

    [HttpPost("{id:guid}/variants")]
    public async Task<ActionResult<VariantResponse>> CreateVariant(Guid id, [FromBody] CreateVariantRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _products.CreateVariantAsync(id, request, ct));

    [HttpPut("{id:guid}/variants/{variantId:guid}")]
    public async Task<ActionResult<VariantResponse>> UpdateVariant(Guid id, Guid variantId, [FromBody] UpdateVariantRequest request, CancellationToken ct)
        => Ok(await _products.UpdateVariantAsync(id, variantId, request, ct));

    [HttpDelete("{id:guid}/variants/{variantId:guid}")]
    public async Task<IActionResult> DeleteVariant(Guid id, Guid variantId, CancellationToken ct)
    {
        await _products.DeleteVariantAsync(id, variantId, ct);
        return NoContent();
    }

    // ---- per-product listings of commercial data --------------------------

    [HttpGet("{id:guid}/offers")]
    public async Task<ActionResult<IReadOnlyList<OfferAdminResponse>>> Offers(Guid id, CancellationToken ct) => Ok(await _offers.ListForProductAsync(id, ct));

    [HttpGet("{id:guid}/availability")]
    public async Task<ActionResult<IReadOnlyList<AvailabilityResponse>>> Availability(Guid id, CancellationToken ct) => Ok(await _availability.ListForProductAsync(id, ct));

    [HttpGet("{id:guid}/relationships")]
    public async Task<ActionResult<IReadOnlyList<RelationshipResponse>>> Relationships(Guid id, [FromQuery] RelationshipType? type, CancellationToken ct) => Ok(await _relationships.ListForProductAsync(id, type, ct));

    [HttpGet("{id:guid}/media")]
    public async Task<ActionResult<IReadOnlyList<MediaResponse>>> Media(Guid id, CancellationToken ct) => Ok(await _media.ListForProductAsync(id, ct));
}
