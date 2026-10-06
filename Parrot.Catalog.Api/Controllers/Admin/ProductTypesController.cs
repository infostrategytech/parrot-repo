using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;

namespace Parrot.Catalog.Api.Controllers.Admin;

[ApiController]
[Route("api/catalog/admin/types")]
[Authorize(Policy = CatalogPolicies.Admin)]
[EnableRateLimiting(RateLimiting.Write)]
[Produces("application/json")]
public sealed class ProductTypesController : ControllerBase
{
    private readonly IProductTypeAdminService _service;

    public ProductTypesController(IProductTypeAdminService service)
    {
        _service = service;
    }

    [HttpPost]
    [EndpointSummary("Create a product type with its first version of attribute definitions.")]
    public async Task<ActionResult<ProductTypeResponse>> Create([FromBody] CreateProductTypeRequest request, CancellationToken ct)
    {
        ProductTypeResponse created = await _service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet]
    [Authorize(Policy = CatalogPolicies.Write)]
    public async Task<ActionResult<IReadOnlyList<ProductTypeResponse>>> List(CancellationToken ct) => Ok(await _service.ListAsync(ct));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = CatalogPolicies.Write)]
    [EndpointSummary("A type and the definitions of one version (default: the current one).")]
    public async Task<ActionResult<ProductTypeResponse>> Get(Guid id, [FromQuery] int? version, CancellationToken ct) => Ok(await _service.GetAsync(id, version, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProductTypeResponse>> Update(Guid id, [FromBody] UpdateProductTypeRequest request, CancellationToken ct) => Ok(await _service.UpdateAsync(id, request, ct));

    [HttpGet("{id:guid}/schema")]
    [Authorize(Policy = CatalogPolicies.Write)]
    [EndpointSummary("JSON Schema + UI hints for rendering the admin form. Consumers must never hardcode attribute keys.")]
    public async Task<ActionResult<TypeSchemaResponse>> Schema(Guid id, [FromQuery] int? version, CancellationToken ct) => Ok(await _service.GetSchemaAsync(id, version, ct));

    [HttpPost("{id:guid}/attributes")]
    [EndpointSummary("Add an attribute to the current version (non-breaking).")]
    public async Task<ActionResult<AttributeDefinitionResponse>> AddAttribute(Guid id, [FromBody] AttributeDefinitionInput input, CancellationToken ct)
        => Ok(await _service.AddAttributeAsync(id, input, ct));

    [HttpPut("{id:guid}/attributes/{definitionId:guid}")]
    [EndpointSummary("Non-breaking edits to a definition. Changing data_type or removing a key needs a new version.")]
    public async Task<ActionResult<AttributeDefinitionResponse>> UpdateAttribute(Guid id, Guid definitionId, [FromBody] UpdateAttributeDefinitionRequest request, CancellationToken ct)
        => Ok(await _service.UpdateAttributeAsync(id, definitionId, request, ct));

    [HttpPost("{id:guid}/versions")]
    [EndpointSummary("Create the next type version. Existing products keep their pinned version until migrated.")]
    public async Task<ActionResult<ProductTypeResponse>> CreateVersion(Guid id, [FromBody] CreateTypeVersionRequest request, CancellationToken ct)
        => Ok(await _service.CreateVersionAsync(id, request, ct));
}
