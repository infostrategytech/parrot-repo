using Parrot.Domain.Entities;

namespace Parrot.Application.Wallets;

/// <summary>What a wallet has taken in and paid out, in kobo, over all its history.</summary>
public sealed record WalletTotals(long InMinor, long OutMinor);

/// <summary>Workspaces' wallets. A wallet is made with its workspace (WorkspaceRepository.TryAddAsync).</summary>
public interface IWalletRepository
{
    /// <summary>
    /// The workspace's wallet, made empty if it has none yet: a workspace from before wallets, or one
    /// whose wallet the migration's backfill missed. Safe to call at once from several places; only one
    /// wallet is ever made. Null when there's no such workspace.
    /// </summary>
    Task<WorkspaceWallet?> GetOrCreateAsync(string workspaceId, CancellationToken ct);

    /// <summary>The sum of the wallet's credits and of its debits.</summary>
    Task<WalletTotals> TotalsAsync(string workspaceId, CancellationToken ct);

    /// <summary>The wallet's ledger, newest first, one page of it, with how many entries there are in all.</summary>
    Task<(IReadOnlyList<WalletTransaction> Items, int Total)> ListTransactionsAsync(
        string workspaceId, WalletDirection? direction, int skip, int take, CancellationToken ct);
}
