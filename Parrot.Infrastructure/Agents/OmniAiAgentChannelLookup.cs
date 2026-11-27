using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.Agents;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.Agents;

/// <summary>
/// Checks whether an agent is live on a channel by asking the AI service.
/// Results are cached per workspace for 30 s — agents rarely change and
/// the cache stays fresh enough for routing decisions.
/// With the workspace's switch for agents sharing a number on (MultiAgentSwitch, temporary), the answer is for the
/// connection the message came in on: two numbers of one kind can have different agents, or none. Off, it is for the
/// channel kind, as before.
/// Returns false when OmniAi is disabled or unreachable so message processing
/// degrades gracefully (CommsType = UserToTenant) rather than breaking.
/// </summary>
public sealed class OmniAiAgentChannelLookup(
    IAgentOrchestratorClient orchestrator,
    IMultiAgentSwitchStore switches,
    IMemoryCache cache,
    IOptions<OmniAiSettings> settings,
    ILogger<OmniAiAgentChannelLookup> logger) : IAgentChannelLookup
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    public async Task<bool> IsAgentLiveAsync(string workspaceId, string channel, Guid? connectionId, CancellationToken ct)
    {
        if (connectionId is { } connection && await SharesNumbersAsync(workspaceId, ct))
            return await IsLiveOnConnectionAsync(workspaceId, connection, ct);

        string cacheKey = $"channel_agents:{workspaceId}";
        bool fromCache = true;
        if (!cache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, ConversationAgentRef>? map) || map is null)
        {
            fromCache = false;
            try
            {
                map = await orchestrator.GetChannelAgentMapAsync(workspaceId, ct);
                cache.Set(cacheKey, map, FreshFor);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "channel_agent_lookup.unavailable WorkspaceId={WorkspaceId} — routing as UserToTenant", workspaceId);
                return false;
            }
        }
        bool found = map.ContainsKey(channel);
        logger.LogDebug(
            "channel_agent_lookup.result WorkspaceId={WorkspaceId} Channel={Channel} MapKeys=[{MapKeys}] Found={Found} FromCache={FromCache}",
            workspaceId, channel, string.Join(", ", map.Keys), found, fromCache);
        return found;
    }

    // Read for every message, so turning the switch off takes effect at once. Unread, it counts as off: routing by
    // channel kind still works for one agent per number.
    private async Task<bool> SharesNumbersAsync(string workspaceId, CancellationToken ct)
    {
        if (!MultiAgentSwitch.IsOffered(settings.Value)) return false;
        try
        {
            return await switches.IsOnAsync(workspaceId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "channel_agent_lookup.switch_unavailable WorkspaceId={WorkspaceId} — routing by channel kind", workspaceId);
            return false;
        }
    }

    private async Task<bool> IsLiveOnConnectionAsync(string workspaceId, Guid connectionId, CancellationToken ct)
    {
        string cacheKey = $"connection_agents:{workspaceId}";
        if (!cache.TryGetValue(cacheKey, out IReadOnlyDictionary<Guid, IReadOnlyList<ConversationAgentRef>>? agents) || agents is null)
        {
            try
            {
                agents = await orchestrator.GetConnectionAgentsAsync(workspaceId, ct);
                cache.Set(cacheKey, agents, FreshFor);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "connection_agent_lookup.unavailable WorkspaceId={WorkspaceId} ConnectionId={ConnectionId} — routing as UserToTenant",
                    workspaceId, connectionId);
                return false;
            }
        }

        bool found = agents.TryGetValue(connectionId, out IReadOnlyList<ConversationAgentRef>? onIt) && onIt.Count > 0;
        logger.LogDebug("connection_agent_lookup.result WorkspaceId={WorkspaceId} ConnectionId={ConnectionId} Agents={Agents} Found={Found}",
            workspaceId, connectionId, onIt?.Count ?? 0, found);
        return found;
    }
}
