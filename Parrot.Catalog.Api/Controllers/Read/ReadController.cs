using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Catalog.Api.Middleware;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Api.Controllers.Read;

/// <summary>Published data only, through the reader role. Every response carries an ETag; cached by tenant.</summary>
[ApiController]
[Route("api/catalog")]
[Authorize(Policy = CatalogPolicies.Read)]
[EnableRateLimiting(RateLimiting.Read)]
[ServiceFilter(typeof(ReadCacheFilter))]
[Produces("application/json")]
public sealed class ReadController : ControllerBase
{
    private readonly ICatalogReadService _read;

    public ReadController(ICatalogReadService read)
    {
        _read = read;
    }

    [HttpGet("products")]
    [EndpointSummary("Published products, newest first, cursor paginated.")]
    public async Task<ActionResult<PagedResponse<ProductSummary>>> Products(
        [FromQuery(Name = "catalog_id")] Guid? catalogId,
        [FromQuery(Name = "category_id")] Guid? categoryId,
        [FromQuery(Name = "product_type_id")] Guid? productTypeId,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken ct)
        => Ok(await _read.ListProductsAsync(new ReadListQuery(catalogId, categoryId, productTypeId, cursor, limit), ct));

    [HttpGet("products/{id:guid}")]
    public async Task<ActionResult<ProductDetail>> Product(Guid id, CancellationToken ct) => Ok(await _read.GetProductAsync(id, ct));

    [HttpGet("products/{id:guid}/offers")]
    [EndpointSummary("Active offers and their active prices. as_of answers 'what did it cost on that date'.")]
    public async Task<ActionResult<IReadOnlyList<OfferView>>> Offers(Guid id, [FromQuery(Name = "as_of")] DateTime? asOf, CancellationToken ct)
        => Ok(await _read.GetOffersAsync(id, asOf, ct));

    [HttpGet("products/{id:guid}/availability")]
    public async Task<ActionResult<IReadOnlyList<AvailabilityView>>> Availability(Guid id, [FromQuery] string? region, [FromQuery(Name = "as_of")] DateTime? asOf, CancellationToken ct)
        => Ok(await _read.GetAvailabilityAsync(id, region, asOf, ct));

    [HttpGet("products/{id:guid}/related")]
    public async Task<ActionResult<IReadOnlyList<RelatedProductView>>> Related(Guid id, [FromQuery] RelationshipType? type, CancellationToken ct)
        => Ok(await _read.GetRelatedAsync(id, type, ct));

    [HttpGet("products/{id:guid}/document")]
    [EndpointSummary("The canonical text document rendered at publish time, with its content hash.")]
    public async Task<ActionResult<DocumentView>> Document(Guid id, CancellationToken ct) => Ok(await _read.GetDocumentAsync(id, ct));

    [HttpPost("search")]
    [EnableRateLimiting(RateLimiting.Search)]
    [EndpointSummary("Deterministic search: full text + registry-validated filters compiled to parameterised SQL over the derived index.")]
    public async Task<ActionResult<SearchResponse>> Search([FromBody] SearchRequest request, CancellationToken ct)
        => Ok(await _read.SearchAsync(request, Limits.MaxPageSize, ct));

    [HttpPost("compare")]
    [EndpointSummary("Attributes of 2-10 products aligned by registry key, label and unit.")]
    public async Task<ActionResult<CompareResponse>> Compare([FromBody] CompareRequest request, CancellationToken ct)
        => Ok(await _read.CompareAsync(request, ct));
}
