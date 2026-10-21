using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// What the AI orchestrator asks when it needs to confirm a connection belongs
/// to the tenant it is acting for. Only a service token opens it; a session
/// token, which never carries that scope, is refused.
/// </summary>
[ApiController]
[Route("api/internal/connections")]
[Authorize(Policy = ServiceScopes.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "internal.connections")]
public sealed class InternalConnectionsController(IConnectionService connections) : ControllerBase
{
    /// <summary>
    /// Confirms that a connection belongs to the tenant and returns its channel key.
    /// 404 is returned both when the connection does not exist and when it belongs
    /// to a different tenant, so that connection ids cannot be enumerated.
    /// 403 is returned when the connection belongs to the same tenant but a different workspace.
    /// </summary>
    [HttpGet("{id:guid}/validate")]
    public async Task<IActionResult> Validate(Guid id, CancellationToken cancellationToken)
    {
        string workspaceId = Tenant();
        ConnectionValidationResult result = await connections.ValidateForWorkspaceAsync(id, workspaceId, cancellationToken);
        return Ok(new { result.Id, result.ChannelKey, result.WorkspaceId });
    }

    private string Tenant() =>
        User.FindFirstValue(ServiceScopes.TenantClaim)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedException("The service token names no tenant.");
}
