using Microsoft.EntityFrameworkCore;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Wallets;

/// <summary>
/// Writes a ledger entry and moves the wallet's balance with it. Callers run it inside their own transaction
/// alongside whatever caused the money to move (an order marked paid), so either both are saved or neither.
/// </summary>
internal static class WalletLedger
{
    /// <summary>
    /// Adds <paramref name="entry"/> and, when it went through (Success), moves its workspace's balance by its
    /// amount; a Failed one is recorded with the balance as it stands. Fills in BalanceAfterMinor. A workspace from before wallets gets one first. False when an entry with the
    /// same reference is already there: the event was counted before, and nothing changes.
    /// </summary>
    public static async Task<bool> PostAsync(ApplicationDbContext db, WalletTransaction entry, CancellationToken ct)
    {
        if (entry.AmountMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "A ledger entry moves more than nothing.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "WorkspaceWallets" ("WorkspaceId", "Currency", "BalanceMinor", "CreatedAt")
            VALUES ({entry.WorkspaceId}, {WorkspaceWallet.Naira}, 0, CURRENT_TIMESTAMP)
            ON CONFLICT ("WorkspaceId") DO NOTHING
            """,
            ct);

        // The entry first, so a second go at the same event stops here; the balance only moves for an entry just added.
        int added = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "WalletTransactions" ("Id", "WorkspaceId", "Type", "Status", "Kind", "AmountMinor", "BalanceAfterMinor", "Currency",
                "Reference", "PaymentOrderId", "ProviderTransactionId", "Description", "ActionBy", "CreatedAt")
            VALUES ({entry.Id}, {entry.WorkspaceId}, {entry.Type.ToString()}, {entry.Status.ToString()}, {entry.Kind.ToString()}, {entry.AmountMinor}, 0,
                {entry.Currency}, {entry.Reference}, {entry.PaymentOrderId}, {entry.ProviderTransactionId}, {entry.Description}, {entry.ActionBy}, {entry.CreatedAt})
            ON CONFLICT ("Reference") DO NOTHING
            """,
            ct);
        if (added == 0)
        {
            return false;
        }

        // A failed one moves nothing: the update still takes the wallet's lock, so its balance-after is in line with the rest.
        long change = entry.Status != WalletTransactionStatus.Success ? 0
            : entry.Type == WalletTransactionType.Credit ? entry.AmountMinor : -entry.AmountMinor;

        // The update locks the wallet's row until the transaction ends, so entries for one wallet line up and
        // each one's balance-after is right. A debit past zero breaks the wallet's check and fails the lot.
        List<long> after = await db.Database
            .SqlQuery<long>(
                $"""
                UPDATE "WorkspaceWallets" SET "BalanceMinor" = "BalanceMinor" + {change},
                    "UpdatedAt" = CASE WHEN {change} = 0 THEN "UpdatedAt" ELSE {entry.CreatedAt} END
                WHERE "WorkspaceId" = {entry.WorkspaceId}
                RETURNING "BalanceMinor" AS "Value"
                """)
            .ToListAsync(ct);
        entry.BalanceAfterMinor = after.Single();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "WalletTransactions" SET "BalanceAfterMinor" = {entry.BalanceAfterMinor} WHERE "Id" = {entry.Id}""",
            ct);
        return true;
    }
}
