using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;

namespace Parrot.Catalog.Api.Controllers.Admin;

[ApiController]
[Route("api/catalog/admin/catalogs")]
[Authorize(Policy = CatalogPolicies.Admin)]
[EnableRateLimiting(RateLimiting.Write)]
[Produces("application/json")]
public sealed class CatalogsController : ControllerBase
{
    private readonly ICatalogAdminService _service;

    public CatalogsController(ICatalogAdminService service)
    {
        _service = service;
    }

    [HttpPost]
    [EndpointSummary("Create a catalog (regional, B2B/B2C, internal).")]
    public async Task<ActionResult<CatalogResponse>> Create([FromBody] CreateCatalogRequest request, CancellationToken ct)
    {
        CatalogResponse created = await _service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet]
    [Authorize(Policy = CatalogPolicies.Write)]
    public async Task<ActionResult<IReadOnlyList<CatalogResponse>>> List(CancellationToken ct) => Ok(await _service.ListAsync(ct));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = CatalogPolicies.Write)]
    public async Task<ActionResult<CatalogResponse>> Get(Guid id, CancellationToken ct) => Ok(await _service.GetAsync(id, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CatalogResponse>> Update(Guid id, [FromBody] UpdateCatalogRequest request, CancellationToken ct) => Ok(await _service.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
