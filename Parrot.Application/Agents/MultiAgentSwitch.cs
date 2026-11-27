using Microsoft.Extensions.Options;
using Parrot.Application.Contracts.Agents;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Agents;

/// <summary>
/// Temporary, for the live test of several agents sharing a number. Each workspace turns it on and off for itself
/// (off until it does), so a fault can be switched off while it is fixed and the rest of Parrot carries on with one
/// agent per number. Removed, with its setting and store, once the live test has passed.
/// The AI Operations figures don't read it: once the AI service answers through its super agent, every reply names
/// the super agent, and only counting the agents that answered in it keeps each agent's figures right, switch or not.
/// </summary>
public sealed class MultiAgentSwitch(IMultiAgentSwitchStore store, IOptions<OmniAiSettings> settings, IAppLogger<MultiAgentSwitch> logger)
{
    /// <summary>The server gives workspaces the switch (OmniAi:MultiAgentEnabled, on by default).</summary>
    public static bool IsOffered(OmniAiSettings settings) => settings.MultiAgentEnabled;

    /// <summary>Several agents may share the workspace's numbers now.</summary>
    public async Task<bool> IsOnAsync(string workspaceId, CancellationToken cancellationToken) =>
        IsOffered(settings.Value) && await store.IsOnAsync(workspaceId, cancellationToken);

    public async Task<MultiAgentSwitchResponse> GetAsync(string workspaceId, CancellationToken cancellationToken)
    {
        bool offered = IsOffered(settings.Value);
        return new MultiAgentSwitchResponse(offered, offered && await store.IsOnAsync(workspaceId, cancellationToken));
    }

    public async Task<MultiAgentSwitchResponse> SetAsync(string workspaceId, string userId, SetMultiAgentSwitchRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsOffered(settings.Value))
            throw new ValidationException("Agents sharing a number isn't available right now.");

        await store.SetAsync(workspaceId, request.IsOn, userId, cancellationToken);
        logger.LogInformation("multi_agent_switch.changed WorkspaceId={WorkspaceId} IsOn={IsOn} UserId={UserId}", workspaceId, request.IsOn, userId);
        return new MultiAgentSwitchResponse(true, request.IsOn);
    }
}
