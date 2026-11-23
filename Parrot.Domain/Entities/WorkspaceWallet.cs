namespace Parrot.Domain.Entities;

/// <summary>
/// What a workspace's customers have paid it, held for the business. Every payment lands in the
/// platform's one Accelerate account, so this is the platform's record of which part of that money is
/// the business's: a ledger here, not an account at Accelerate. Each workspace has exactly one, made
/// with the workspace. Amounts are in the currency's minor unit (kobo), as on payment orders.
/// </summary>
public class WorkspaceWallet
{
    public const string Naira = "NGN";

    /// <summary>The workspace it belongs to, which is also its key: one wallet per workspace.</summary>
    public required string WorkspaceId { get; set; }

    public string Currency { get; set; } = Naira;

    /// <summary>What the business has to its name, in kobo. Never below zero.</summary>
    public long BalanceMinor { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    /// <summary>An empty wallet for a workspace being made.</summary>
    public static WorkspaceWallet For(string workspaceId) => new () { WorkspaceId = workspaceId };
}
