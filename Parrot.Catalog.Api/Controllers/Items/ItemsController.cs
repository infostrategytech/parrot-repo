using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;

namespace Parrot.Catalog.Api.Controllers.Items;

/// <summary>
/// What the dashboard's Products &amp; Services page calls: one item per thing the
/// workspace sells, drafts included, saved whole in one transaction. Read through the
/// writer's tables, so it is never served from the published-data cache.
/// </summary>
[ApiController]
[Route("api/catalog")]
[Produces("application/json")]
public sealed class ItemsController : ControllerBase
{
    private readonly IItemService _items;

    public ItemsController(IItemService items)
    {
        _items = items;
    }

    [HttpGet("items")]
    [Authorize(Policy = CatalogPolicies.Read)]
    [EnableRateLimiting(RateLimiting.Read)]
    [EndpointSummary("A page of items with the counts for each tab. status is all, available, unavailable or draft.")]
    public async Task<ActionResult<ItemListResponse>> List(
        [FromQuery] ItemFilter status,
        [FromQuery(Name = "category_id")] Guid? categoryId,
        [FromQuery] string? q,
        [FromQuery] int? page,
        [FromQuery(Name = "page_size")] int? pageSize,
        CancellationToken ct)
        => Ok(await _items.ListAsync(new ItemListQuery(status, categoryId, q, page, pageSize), ct));

    [HttpGet("items/{id:guid}")]
    [Authorize(Policy = CatalogPolicies.Read)]
    [EnableRateLimiting(RateLimiting.Read)]
    public async Task<ActionResult<ItemResponse>> Get(Guid id, CancellationToken ct) => Ok(await _items.GetAsync(id, ct));

    [HttpPost("items")]
    [Authorize(Policy = CatalogPolicies.Write)]
    [EnableRateLimiting(RateLimiting.Write)]
    [EndpointSummary("Add an item: the product, its prices and its availability in one transaction. status published also makes it live.")]
    public async Task<ActionResult<ItemResponse>> Create([FromBody] SaveItemRequest request, CancellationToken ct)
    {
        ItemResponse created = await _items.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("items/{id:guid}")]
    [Authorize(Policy = CatalogPolicies.Write)]
    [EnableRateLimiting(RateLimiting.Write)]
    [EndpointSummary("Save the whole form. Variants not sent are removed; a live item cannot go back to draft.")]
    public async Task<ActionResult<ItemResponse>> Update(Guid id, [FromBody] SaveItemRequest request, CancellationToken ct) => Ok(await _items.UpdateAsync(id, request, ct));

    [HttpPatch("items/{id:guid}/availability")]
    [Authorize(Policy = CatalogPolicies.Write)]
    [EnableRateLimiting(RateLimiting.Write)]
    [EndpointSummary("Change only the availability, as the list's dropdown does.")]
    public async Task<ActionResult<ItemResponse>> SetAvailability(Guid id, [FromBody] ItemAvailabilityInput request, CancellationToken ct)
        => Ok(await _items.SetAvailabilityAsync(id, request, ct));

    [HttpDelete("items/{id:guid}")]
    [Authorize(Policy = CatalogPolicies.Write)]
    [EnableRateLimiting(RateLimiting.Write)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _items.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("categories")]
    [Authorize(Policy = CatalogPolicies.Read)]
    [EnableRateLimiting(RateLimiting.Read)]
    public async Task<ActionResult<IReadOnlyList<ItemCategory>>> Categories(CancellationToken ct) => Ok(await _items.ListCategoriesAsync(ct));

    [HttpPost("categories")]
    [Authorize(Policy = CatalogPolicies.Write)]
    [EnableRateLimiting(RateLimiting.Write)]
    public async Task<ActionResult<ItemCategory>> CreateCategory([FromBody] CreateItemCategoryRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _items.CreateCategoryAsync(request, ct));
}
