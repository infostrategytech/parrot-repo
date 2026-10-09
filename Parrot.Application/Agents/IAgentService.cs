using Parrot.Application.DTOs.Agents;

namespace Parrot.Application.Agents;

public interface IAgentService
{
    Task<AgentResponse> CreateAsync(string userId, AgentInfo request);

    Task<IReadOnlyList<AgentResponse>> GetAllAsync(string userId);

    Task<AgentResponse> GetByIdAsync(string userId, Guid agentId);

    /// <summary>Saves everything about an existing agent except whether it is on; see <see cref="SetLiveAsync"/>.</summary>
    Task<AgentResponse> UpdateAsync(string userId, Guid agentId, AgentInfo request);

    /// <summary>Turns an agent on or off. Either way it stops being a draft: the owner has decided about it.</summary>
    Task<AgentResponse> SetLiveAsync(string userId, Guid agentId, bool on);

    /// <summary>Deletes an agent for the owner. The row stays, marked deleted, so past activity still names it.</summary>
    Task DeleteAsync(string userId, Guid agentId);

    /// <summary>The turned-on agent answering <paramref name="channel"/> for the owner, or null when none does.</summary>
    Task<AgentResponse?> ResolveForChannelAsync(string ownerUserId, string channel);
}
