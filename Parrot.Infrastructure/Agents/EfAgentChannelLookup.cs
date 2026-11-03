using Microsoft.EntityFrameworkCore;
using Parrot.Application.Agents;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Agents;

public sealed class EfAgentChannelLookup(IDbContextFactory<ApplicationDbContext> factory) : IAgentChannelLookup
{
    public async Task<bool> IsAgentLiveAsync(string workspaceId, string channel, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        List<Agent> live = await db.Agents
            .Where(a => a.WorkspaceId == workspaceId && a.DeletedAt == null && a.IsActive && !a.IsDraft)
            .ToListAsync(ct);
        return live.Any(a => a.Channels.Contains(channel, StringComparer.OrdinalIgnoreCase));
    }
}
