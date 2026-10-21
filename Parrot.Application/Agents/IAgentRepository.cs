using Parrot.Domain.Entities;

namespace Parrot.Application.Agents;

/// <summary>The owner's agents. Deleted agents are kept as rows but never returned.</summary>
public interface IAgentRepository
{
    Task AddAsync(Agent agent);

    Task UpdateAsync(Agent agent);

    /// <summary>The owner's agents, newest first: on, off and drafts alike.</summary>
    Task<IReadOnlyList<Agent>> GetByWorkspaceAsync(string workspaceId);

    Task<Agent?> GetByIdAsync(Guid id);

    /// <summary>The turned-on agent that answers <paramref name="channel"/> for the owner, or null when none does.</summary>
    Task<Agent?> GetLiveForChannelAsync(string workspaceId, string channel);
}
