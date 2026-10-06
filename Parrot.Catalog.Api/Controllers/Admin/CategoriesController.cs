using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;

namespace Parrot.Catalog.Api.Controllers.Admin;

[ApiController]
[Route("api/catalog/admin/categories")]
[Authorize(Policy = CatalogPolicies.Write)]
[EnableRateLimiting(RateLimiting.Write)]
[Produces("application/json")]
public sealed class CategoriesController : ControllerBase
{
    private readonly ICategoryAdminService _service;

    public CategoriesController(ICategoryAdminService service)
    {
        _service = service;
    }

    [HttpPost]
    public async Task<ActionResult<CategoryResponse>> Create([FromBody] CreateCategoryRequest request, CancellationToken ct)
    {
        CategoryResponse created = await _service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CategoryResponse>>> List([FromQuery(Name = "catalog_id")] Guid? catalogId, CancellationToken ct) => Ok(await _service.ListAsync(catalogId, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CategoryResponse>> Get(Guid id, CancellationToken ct) => Ok(await _service.GetAsync(id, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CategoryResponse>> Update(Guid id, [FromBody] UpdateCategoryRequest request, CancellationToken ct) => Ok(await _service.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
