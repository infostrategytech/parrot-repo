using Microsoft.EntityFrameworkCore;
using Parrot.Application.Wallets;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Wallets;

public sealed class EfWalletRepository(ApplicationDbContext db) : IWalletRepository
{
    public async Task<WorkspaceWallet?> GetOrCreateAsync(string workspaceId, CancellationToken ct)
    {
        WorkspaceWallet? wallet = await Find(workspaceId, ct);
        if (wallet is not null)
        {
            return wallet;
        }

        // Made only for a workspace that exists, and only once however many callers race here.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "WorkspaceWallets" ("WorkspaceId", "Currency", "BalanceMinor", "CreatedAt")
            SELECT "Id", {WorkspaceWallet.Naira}, 0, CURRENT_TIMESTAMP FROM "Workspaces" WHERE "Id" = {workspaceId}
            ON CONFLICT ("WorkspaceId") DO NOTHING
            """,
            ct);

        return await Find(workspaceId, ct);
    }

    private Task<WorkspaceWallet?> Find(string workspaceId, CancellationToken ct) =>
        db.WorkspaceWallets.AsNoTracking().FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
}
