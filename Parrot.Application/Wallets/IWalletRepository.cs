using Parrot.Domain.Entities;

namespace Parrot.Application.Wallets;

/// <summary>Workspaces' wallets. A wallet is made with its workspace (WorkspaceRepository.TryAddAsync).</summary>
public interface IWalletRepository
{
    /// <summary>
    /// The workspace's wallet, made empty if it has none yet: a workspace from before wallets, or one
    /// whose wallet the migration's backfill missed. Safe to call at once from several places; only one
    /// wallet is ever made. Null when there's no such workspace.
    /// </summary>
    Task<WorkspaceWallet?> GetOrCreateAsync(string workspaceId, CancellationToken ct);
}
