namespace Parrot.Domain.Entities;

/// <summary>Which way money moved in a wallet.</summary>
public enum WalletDirection
{
    /// <summary>Money in: the balance went up.</summary>
    Credit,

    /// <summary>Money out: the balance went down.</summary>
    Debit,
}

/// <summary>Why money moved.</summary>
public enum WalletTransactionKind
{
    /// <summary>A customer paid an order (a credit).</summary>
    Payment,

    /// <summary>A charge taken from the balance (a debit).</summary>
    Fee,

    /// <summary>Money paid out to the business (a debit).</summary>
    Payout,

    /// <summary>A correction made by hand, either way.</summary>
    Adjustment,
}

/// <summary>
/// One line of a workspace wallet's ledger: money in or out, why, and the balance just after it.
/// Entries are only ever added, never changed, so the wallet's balance is always the sum of its
/// credits less its debits. Reference names what caused the entry (for a payment, the order's prt_
/// reference) and is unique, so the same event can never be counted twice.
/// </summary>
public class WalletTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string WorkspaceId { get; set; }

    public WalletDirection Direction { get; set; }

    public WalletTransactionKind Kind { get; set; }

    /// <summary>How much moved, in kobo. Always above zero: Direction says which way.</summary>
    public long AmountMinor { get; set; }

    /// <summary>The wallet's balance just after this entry, in kobo.</summary>
    public long BalanceAfterMinor { get; set; }

    public string Currency { get; set; } = WorkspaceWallet.Naira;

    /// <summary>What caused it, unique across all wallets: for a payment, the order's reference.</summary>
    public required string Reference { get; set; }

    /// <summary>The order paid, for a payment.</summary>
    public Guid? PaymentOrderId { get; set; }

    /// <summary>The provider's own id for the money (Accelerate's transaction id).</summary>
    public string? ProviderTransactionId { get; set; }

    /// <summary>In words, for the business: "Payment from Ada for 2 x Meat Pie".</summary>
    public required string Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
