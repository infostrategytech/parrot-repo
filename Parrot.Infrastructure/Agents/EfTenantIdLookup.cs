using Microsoft.EntityFrameworkCore;
using Parrot.Application.Agents;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Agents;

public sealed class EfTenantIdLookup(IDbContextFactory<ApplicationDbContext> factory) : ITenantIdLookup
{
    public async Task<string?> FindOwnerIdAsync(string workspaceId, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        Workspace? workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
        return workspace?.OwnerId;
    }
}
