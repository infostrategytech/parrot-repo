using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Catalog.Api.Security;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;

namespace Parrot.Catalog.Api.Controllers.Ai;

/// <summary>
/// The narrow surface an LLM agent calls. Tenant comes from the token, never from the
/// body; results strip internal fields and wrap tenant text as untrusted data.
/// </summary>
[ApiController]
[Route("api/catalog/ai/tools")]
[Authorize(Policy = CatalogPolicies.AiTools)]
[EnableRateLimiting(RateLimiting.Ai)]
[Produces("application/json")]
public sealed class AiToolsController : ControllerBase
{
    private readonly IAiToolService _tools;

    public AiToolsController(IAiToolService tools)
    {
        _tools = tools;
    }

    [HttpGet("schemas")]
    [EndpointSummary("The tool manifest: names, descriptions and JSON input schemas to hand to the LLM verbatim.")]
    public ActionResult<JsonElement> Schemas() => Ok(AiToolSchemas.Manifest());

    [HttpPost("search_catalog")]
    public async Task<ActionResult<AiToolResponse<AiSearchResult>>> SearchCatalog([FromBody] SearchCatalogToolRequest request, CancellationToken ct) => Ok(await _tools.SearchCatalogAsync(request, ct));

    [HttpPost("get_product")]
    public async Task<ActionResult<AiToolResponse<AiProduct>>> GetProduct([FromBody] GetProductToolRequest request, CancellationToken ct) => Ok(await _tools.GetProductAsync(request, ct));

    [HttpPost("get_product_offers")]
    public async Task<ActionResult<AiToolResponse<AiOffersResult>>> GetProductOffers([FromBody] GetProductOffersToolRequest request, CancellationToken ct) => Ok(await _tools.GetProductOffersAsync(request, ct));

    [HttpPost("get_prices")]
    public async Task<ActionResult<AiToolResponse<AiPricesResult>>> GetPrices([FromBody] GetPricesToolRequest request, CancellationToken ct) => Ok(await _tools.GetPricesAsync(request, ct));

    [HttpPost("get_product_availability")]
    public async Task<ActionResult<AiToolResponse<AiAvailabilityResult>>> GetProductAvailability([FromBody] GetProductAvailabilityToolRequest request, CancellationToken ct) => Ok(await _tools.GetProductAvailabilityAsync(request, ct));

    [HttpPost("compare_products")]
    public async Task<ActionResult<AiToolResponse<AiCompareResult>>> CompareProducts([FromBody] CompareProductsToolRequest request, CancellationToken ct) => Ok(await _tools.CompareProductsAsync(request, ct));

    [HttpPost("get_related_products")]
    public async Task<ActionResult<AiToolResponse<AiRelatedResult>>> GetRelatedProducts([FromBody] GetRelatedProductsToolRequest request, CancellationToken ct) => Ok(await _tools.GetRelatedProductsAsync(request, ct));
}
