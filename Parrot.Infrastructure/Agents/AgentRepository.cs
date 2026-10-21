using Microsoft.EntityFrameworkCore;
using Parrot.Application.Agents;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Agents;

public class AgentRepository : IAgentRepository
{
    private readonly ApplicationDbContext _db;

    public AgentRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Agent agent)
    {
        _db.Agents.Add(agent);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Agent agent)
    {
        _db.Agents.Update(agent);
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<Agent>> GetByWorkspaceAsync(string workspaceId) =>
        await _db.Agents
            .Where(a => a.WorkspaceId == workspaceId && a.DeletedAt == null)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();

    public async Task<Agent?> GetByIdAsync(Guid id) =>
        await _db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null);

    public async Task<Agent?> GetLiveForChannelAsync(string workspaceId, string channel)
    {
        // The channels are one text column, so the match is made here rather
        // than in SQL. An owner has a handful of agents; this reads them all.
        List<Agent> live = await _db.Agents
            .Where(a => a.WorkspaceId == workspaceId && a.DeletedAt == null && a.IsActive && !a.IsDraft)
            .OrderByDescending(a => a.UpdatedAt ?? a.CreatedAt)
            .ToListAsync();

        return live.FirstOrDefault(a => a.Channels.Contains(channel, StringComparer.OrdinalIgnoreCase));
    }
}
