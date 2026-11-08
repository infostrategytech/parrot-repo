using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.Conversations;
using Parrot.Application.DTOs.Agents;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
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
public sealed class InternalAgentsController(
    IAgentOrchestratorClient orchestrator,
    IMemoryCache cache,
    IWorkspaceRepository workspaces) : ControllerBase
{
    /// <summary>The turned-on agent answering a channel for the tenant, with the business name its persona uses.</summary>
    /// <remarks>
    /// 404 means no agent answers that channel, which is an everyday answer
    /// (the orchestrator caches it), so it is returned directly rather than
    /// thrown and logged as a failure.
    /// </remarks>
    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve([FromQuery] string? channel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channel))
            throw new ValidationException("Supply the channel to resolve an agent for.");

        string owner = Tenant();
        string key = channel.Trim().ToLowerInvariant();

        // The channel→agent map is shared with the inbox controller's cache (same key).
        string cacheKey = $"channel_agents:{owner}";
        if (!cache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, ConversationAgentRef>? agentByChannel) || agentByChannel is null)
        {
            agentByChannel = await orchestrator.GetChannelAgentMapAsync(owner, cancellationToken);
            cache.Set(cacheKey, agentByChannel, TimeSpan.FromSeconds(30));
        }

        if (!agentByChannel.TryGetValue(key, out ConversationAgentRef? agentRef))
        {
            return NotFound(new ErrorResponse
            {
                ErrorCode = "NOT_FOUND",
                Message = $"No live agent answers {key}.",
            });
        }

        // All channels this agent covers (the map may carry several entries for the same agent id).
        List<string> agentChannels = [.. agentByChannel.Where(e => e.Value.Id == agentRef.Id).Select(e => e.Key)];

        Workspace? workspace = await workspaces.GetByIdAsync(owner);
        // The orchestrator owns its own persona settings; we return defaults so the shape is valid.
        // BusinessName and OwnerUserId are what it needs from Parrot's side.
        return Ok(new AgentResolution(
            agentRef.Id,
            agentRef.Name,
            true, // only live agents appear in GetChannelAgentMapAsync
            agentChannels,
            new AgentSettingsDto(),
            1,
            workspace?.Name,
            owner));
    }

    // A service token names the tenant explicitly; its subject is the same id.
    // The tenant here is the owner every row is keyed by: the workspace.
    private string Tenant() =>
        User.FindFirstValue(ServiceScopes.TenantClaim)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedException("The service token names no tenant.");
}
