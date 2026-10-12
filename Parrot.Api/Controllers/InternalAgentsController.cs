using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Agents;
using Parrot.Application.Models;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// What the orchestrator asks this API on a tenant's behalf. Only a service
/// token opens it (the ServiceScope policy); a session token, which never
/// carries that scope, is refused. The tenant is read from the token alone.
/// </summary>
[ApiController]
[Route("api/internal/agents")]
[Authorize(Policy = ServiceScopes.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "internal.agents")]
public sealed class InternalAgentsController(IAgentService agents, UserManager<ApplicationUser> users) : ControllerBase
{
    /// <summary>The turned-on agent answering a channel for the tenant, with the business name its persona uses.</summary>
    /// <remarks>
    /// 404 means no agent answers that channel, which is an everyday answer
    /// (the orchestrator caches it), so it is returned directly rather than
    /// thrown and logged as a failure.
    /// </remarks>
    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve([FromQuery] string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
            throw new ValidationException("Supply the channel to resolve an agent for.");

        string owner = Tenant();
        AgentResponse? agent = await agents.ResolveForChannelAsync(owner, channel);
        if (agent is null)
        {
            return NotFound(new ErrorResponse
            {
                ErrorCode = "NOT_FOUND",
                Message = $"No live agent answers {channel.Trim().ToLowerInvariant()}.",
            });
        }

        ApplicationUser? user = await users.FindByIdAsync(owner);
        return Ok(new AgentResolution(
            agent.Id.ToString(),
            agent.Name,
            agent.IsActive && !agent.IsDraft,
            agent.Channels,
            agent.Settings,
            agent.Version,
            user?.BusinessName,
            owner));
    }

    // A service token names the tenant explicitly; its subject is the same id.
    private string Tenant() =>
        User.FindFirstValue(ServiceScopes.TenantClaim)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedException("The service token names no tenant.");
}
