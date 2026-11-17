using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Domain.Entities;

/// <summary>
/// Stock kept aside for a customer while they pay. It counts against what's left only
/// while it's Held and before <see cref="ExpiresAt"/>, so a hold nobody pays for frees the
/// stock on its own when its time is up, even if nothing ever marks it released. When the
/// payment arrives it's Committed: the quantity comes off the availability row for good.
/// </summary>
public sealed class StockHold : ITenantScoped, ITimestamped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The payment order's reference, the same one the payment provider sends back.</summary>
    public string OrderReference { get; set; } = "";

    public Guid ProductId { get; set; }

    /// <summary>The option held, or null for a product without options.</summary>
    public Guid? VariantId { get; set; }

    public int Quantity { get; set; }

    public StockHoldStatus Status { get; set; } = StockHoldStatus.Held;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
