using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Parrot.Application.Agents;
using Parrot.Application.Conversations;

namespace Parrot.Infrastructure.Agents;

/// <summary>
/// Checks whether an agent is live on a channel by asking the AI service.
/// Results are cached per workspace for 30 s — agents rarely change and
/// the cache stays fresh enough for routing decisions.
/// Returns false when OmniAi is disabled or unreachable so message processing
/// degrades gracefully (CommsType = UserToTenant) rather than breaking.
/// </summary>
public sealed class OmniAiAgentChannelLookup(
    IAgentOrchestratorClient orchestrator,
    IMemoryCache cache,
    ILogger<OmniAiAgentChannelLookup> logger) : IAgentChannelLookup
{
    public async Task<bool> IsAgentLiveAsync(string workspaceId, string channel, CancellationToken ct)
    {
        string cacheKey = $"channel_agents:{workspaceId}";
        bool fromCache = true;
        if (!cache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, ConversationAgentRef>? map) || map is null)
        {
            fromCache = false;
            try
            {
                map = await orchestrator.GetChannelAgentMapAsync(workspaceId, ct);
                cache.Set(cacheKey, map, TimeSpan.FromSeconds(30));
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
}
