using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Application.Auth;
using Parrot.Application.BusinessSources;
using Parrot.Application.Contracts.BusinessSources;
using Parrot.Application.Logging;
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
public sealed class InternalBusinessSourcesController(
    IBusinessSourceCredentialsService credentials,
    IAppLogger<InternalBusinessSourcesController> logger) : ControllerBase
{
    /// <summary>
    /// Returns the decrypted access token and API version for a business source, scoped to the
    /// calling workspace. 404 is returned both when the source does not exist and when it belongs
    /// to a different workspace, so source ids cannot be enumerated across workspaces.
    /// POST per the agreed Omni AI contract (GET is not suitable for credential retrieval).
    /// </summary>
    [HttpPost("{sourceId:guid}/credentials")]
    public async Task<IActionResult> FetchCredentials(Guid sourceId, CancellationToken cancellationToken)
    {
        string workspaceId = Tenant();
        logger.LogInformation("internal.credentials.request SourceId={SourceId} WorkspaceId={WorkspaceId}",
            sourceId, workspaceId);

        BusinessSourceCredentialsResponse response =
            await credentials.GetCredentialsAsync(sourceId, workspaceId, cancellationToken);

        logger.LogInformation("internal.credentials.ok SourceId={SourceId} WorkspaceId={WorkspaceId} ApiVersion={ApiVersion}",
            sourceId, workspaceId, response.ApiVersion);
        return Ok(response);
    }

    private string Tenant() =>
        User.FindFirstValue(ServiceScopes.TenantClaim)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedException("The service token names no tenant.");
}
