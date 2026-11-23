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

    public async Task<WalletTotals> TotalsAsync(string workspaceId, CancellationToken ct)
    {
        var sums = await db.WalletTransactions.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && t.Status == WalletTransactionStatus.Success)
            .GroupBy(t => t.Type)
            .Select(g => new { Type = g.Key, Sum = g.Sum(t => t.AmountMinor) })
            .ToListAsync(ct);
        return new WalletTotals(
            sums.Where(s => s.Type == WalletTransactionType.Credit).Sum(s => s.Sum),
            sums.Where(s => s.Type == WalletTransactionType.Debit).Sum(s => s.Sum));
    }

    public async Task<(IReadOnlyList<WalletTransaction> Items, int Total)> ListTransactionsAsync(
        string workspaceId, WalletTransactionFilter filter, int skip, int take, CancellationToken ct)
    {
        IQueryable<WalletTransaction> query = db.WalletTransactions.AsNoTracking().Where(t => t.WorkspaceId == workspaceId);
        if (filter.Type is { } type)
        {
            query = query.Where(t => t.Type == type);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(t => t.Status == status);
        }

        int total = await query.CountAsync(ct);
        List<WalletTransaction> items = await query
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Skip(skip).Take(take)
            .ToListAsync(ct);
        return (items, total);
    }

    private Task<WorkspaceWallet?> Find(string workspaceId, CancellationToken ct) =>
        db.WorkspaceWallets.AsNoTracking().FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
}
