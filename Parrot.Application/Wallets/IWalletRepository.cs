using Parrot.Domain.Entities;

namespace Parrot.Application.Wallets;

/// <summary>What a wallet has taken in and paid out, in kobo, counting only what went through.</summary>
public sealed record WalletTotals(long InMinor, long OutMinor);

/// <summary>Which ledger entries to list: one type or status only, or null for all.</summary>
public sealed record WalletTransactionFilter(WalletTransactionType? Type = null, WalletTransactionStatus? Status = null);

/// <summary>Workspaces' wallets. A wallet is made with its workspace (WorkspaceRepository.TryAddAsync).</summary>
public interface IWalletRepository
{
    /// <summary>
    /// The workspace's wallet, made empty if it has none yet: a workspace from before wallets, or one
    /// whose wallet the migration's backfill missed. Safe to call at once from several places; only one
    /// wallet is ever made. Null when there's no such workspace.
    /// </summary>
    Task<WorkspaceWallet?> GetOrCreateAsync(string workspaceId, CancellationToken ct);

    /// <summary>The sum of the wallet's successful credits and of its successful debits.</summary>
    Task<WalletTotals> TotalsAsync(string workspaceId, CancellationToken ct);

    /// <summary>The wallet's ledger, newest first, one page of it, with how many entries match in all.</summary>
    Task<(IReadOnlyList<WalletTransaction> Items, int Total)> ListTransactionsAsync(
        string workspaceId, WalletTransactionFilter filter, int skip, int take, CancellationToken ct);
}
