using Microsoft.Extensions.Caching.Memory;
using Parrot.Application.Agents;
using Parrot.Application.Conversations;

namespace Parrot.Infrastructure.Agents;

/// <summary>
/// Checks whether an agent is live on a channel by asking the AI service.
/// Results are cached per workspace for 30 s — agents rarely change and
/// the cache stays fresh enough for routing decisions.
/// Returns false when OmniAi is disabled or unreachable (GetChannelAgentMapAsync
/// returns an empty dict in both cases).
/// </summary>
public sealed class OmniAiAgentChannelLookup(IAgentOrchestratorClient orchestrator, IMemoryCache cache) : IAgentChannelLookup
{
    public async Task<bool> IsAgentLiveAsync(string workspaceId, string channel, CancellationToken ct)
    {
        string cacheKey = $"channel_agents:{workspaceId}";
        if (!cache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, ConversationAgentRef>? map) || map is null)
        {
            map = await orchestrator.GetChannelAgentMapAsync(workspaceId, ct);
            cache.Set(cacheKey, map, TimeSpan.FromSeconds(30));
        }
        return map.ContainsKey(channel);
    }
}
