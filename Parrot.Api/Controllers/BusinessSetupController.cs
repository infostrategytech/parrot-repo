using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.Integrations.BusinessEnrichment;
using Parrot.Application.Teams;

namespace Parrot.Api.Rest.Controllers;

[Route("api/business-setup")]
[ApiController]
[Authorize]
[Monitor(Area = "business-setup", SlowMs = 15000)] // web scraping + LLM enrichment
public class BusinessSetupController : ControllerBase
{
    private readonly IBusinessProfileEnrichmentService _enrichmentService;

    public BusinessSetupController(IBusinessProfileEnrichmentService enrichmentService)
    {
        _enrichmentService = enrichmentService;
    }

    [HttpPost("enrich")]
    [RequirePermission(WorkspacePermissions.WorkspaceManage)]
    [ProducesResponseType(typeof(BusinessEnrichmentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Enrich([FromBody] BusinessEnrichmentRequest request, CancellationToken cancellationToken)
    {
        BusinessEnrichmentResult result = await _enrichmentService.EnrichAsync(request, cancellationToken);
        return Ok(result);
    }
}
