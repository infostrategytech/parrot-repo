using System.Text.Json.Serialization;

namespace Parrot.Domain.Entities;

/// <summary>Which way money moved in a wallet. Sent as its name ("Credit"), as the dashboard reads it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WalletTransactionType>))]
public enum WalletTransactionType
{
    /// <summary>Money in: the balance went up.</summary>
    Credit,

    /// <summary>Money out: the balance went down.</summary>
    Debit,
}

/// <summary>Whether the movement went through. Sent as its name ("Success").</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WalletTransactionStatus>))]
public enum WalletTransactionStatus
{
    /// <summary>It went through: the balance moved by its amount.</summary>
    Success,

    /// <summary>It was tried and didn't go through (a payout the bank refused): kept for the record, the balance didn't move.</summary>
    Failed,
}

/// <summary>Why money moved. Sent as its name ("Payment").</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WalletTransactionKind>))]
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
/// One line of a workspace wallet's ledger: money in or out, why, whether it went through, who did it,
/// and the balance just after it. Entries are only ever added, never changed, so the wallet's balance is
/// always the sum of its successful credits less its successful debits. Reference names what caused the entry (for a payment, the order's prt_
/// reference) and is unique, so the same event can never be counted twice.
/// </summary>
public class WalletTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string WorkspaceId { get; set; }

    /// <summary>Credit (money in) or Debit (money out).</summary>
    public WalletTransactionType Type { get; set; }

    /// <summary>Success, or Failed: a failed one is on record but never moved the balance.</summary>
    public WalletTransactionStatus Status { get; set; } = WalletTransactionStatus.Success;

    /// <summary>
    /// Who made it happen: a user's id when a person did it (a payout they asked for, an adjustment), or a
    /// system actor such as <see cref="AccelerateActor"/> when it happened by itself.
    /// </summary>
    public required string ActionBy { get; set; }

    public WalletTransactionKind Kind { get; set; }

    /// <summary>How much moved, in kobo. Always above zero: Type says which way.</summary>
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

    /// <summary>ActionBy for a payment Accelerate's webhook told us about.</summary>
    public const string AccelerateActor = "system:accelerate";

    /// <summary>Whether ActionBy names the platform rather than a person.</summary>
    public static bool IsSystemActor(string? actionBy) => actionBy?.StartsWith("system:", StringComparison.Ordinal) == true;
}
