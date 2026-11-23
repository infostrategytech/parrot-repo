using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Workspaces;

public sealed class WorkspaceRepository(ApplicationDbContext db) : IWorkspaceRepository
{
    public async Task<Workspace?> GetByIdAsync(string id) =>
        await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id);

    public async Task<IReadOnlyList<Workspace>> ListByOwnerAsync(string ownerId) =>
        await db.Workspaces
            .Where(w => w.OwnerId == ownerId)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync();

    public async Task<bool> TryAddAsync(Workspace workspace)
    {
        // Its wallet is saved with it, so no workspace is ever without one.
        WorkspaceWallet wallet = WorkspaceWallet.For(workspace.Id);
        db.Workspaces.Add(workspace);
        db.WorkspaceWallets.Add(wallet);
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Detached so the losing rows are not retried by the next save on this context.
            db.Entry(workspace).State = EntityState.Detached;
            db.Entry(wallet).State = EntityState.Detached;
            return false;
        }
    }

    public async Task UpdateAsync(Workspace workspace)
    {
        db.Workspaces.Update(workspace);
        await db.SaveChangesAsync();
    }
}
