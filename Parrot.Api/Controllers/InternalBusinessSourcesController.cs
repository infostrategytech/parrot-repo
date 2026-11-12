using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Application.Auth;
using Parrot.Application.BusinessSources;
using Parrot.Application.Contracts.BusinessSources;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// Credential broker for the Omni AI service. Only a service token that carries a tenant claim
/// (workspace id) opens it; every response is scoped to the workspace that tenant claim names.
/// </summary>
[ApiController]
[Route("api/internal/business-sources")]
[Authorize(Policy = ServiceScopes.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "internal.business-sources")]
public sealed class InternalBusinessSourcesController(IBusinessSourceCredentialsService credentials) : ControllerBase
{
    /// <summary>
    /// Returns the decrypted access token and API version for a business source, scoped to the
    /// calling workspace. 404 is returned both when the source does not exist and when it belongs
    /// to a different workspace, so source ids cannot be enumerated across workspaces.
    /// </summary>
    [HttpGet("{sourceId:guid}/credentials")]
    public async Task<IActionResult> GetCredentials(Guid sourceId, CancellationToken cancellationToken)
    {
        string workspaceId = Tenant();
        BusinessSourceCredentialsResponse response =
            await credentials.GetCredentialsAsync(sourceId, workspaceId, cancellationToken);
        return Ok(response);
    }

    private string Tenant() =>
        User.FindFirstValue(ServiceScopes.TenantClaim)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedException("The service token names no tenant.");
}
