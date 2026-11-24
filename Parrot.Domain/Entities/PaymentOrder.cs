namespace Parrot.Domain.Entities;

/// <summary>Where a payment order has got to.</summary>
public enum PaymentOrderStatus
{
    /// <summary>Pay was pressed: the payment link is being made.</summary>
    Creating,

    /// <summary>The agent sent the checkout; the customer hasn't pressed Pay yet.</summary>
    AwaitingCheckout,

    /// <summary>The customer pressed Pay and has the payment link; no payment has been recorded yet.</summary>
    Pending,

    /// <summary>The checkout or its payment link couldn't be made. Nothing can be paid.</summary>
    Failed,

    /// <summary>A newer checkout for the same conversation replaced it.</summary>
    Cancelled,

    /// <summary>Its time ran out unpaid; the stock held for it was let go.</summary>
    Expired,

    /// <summary>Paid in full, and the stock taken off.</summary>
    Paid,

    /// <summary>Paid in full after its hold ran out, and some of the stock had gone to someone else.</summary>
    PaidOutOfStock,

    /// <summary>A payment arrived that doesn't match the order, such as the wrong amount. The business has to look.</summary>
    NeedsAttention,
}

/// <summary>
/// What an agent asked a customer to pay for: a checkout, priced from the live catalog when the
/// agent made it and again when the customer presses Pay. The prices are copied in, so a later
/// price change never rewrites an order once it's being paid.
/// Amounts are kept in the currency's smallest unit (kobo), the unit the payment
/// provider takes, so nothing is rounded twice.
/// </summary>
public class PaymentOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The workspace it belongs to. Every lookup is scoped by it.</summary>
    public required string WorkspaceId { get; set; }

    /// <summary>The agent that made the link.</summary>
    public Guid AgentId { get; set; }

    /// <summary>The conversation it was made in: the connection and the customer's sender id.</summary>
    public Guid ConnectionId { get; set; }

    public required string CustomerSender { get; set; }

    public string? CustomerName { get; set; }

    public string? CustomerEmail { get; set; }

    public string? CustomerPhone { get; set; }

    /// <summary>The agent's note for the business, such as where to deliver. Never sent to the provider.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// The reference the provider is given and sends back with the payment: random, so no
    /// one can guess another order's, and used once.
    /// </summary>
    public required string Reference { get; set; }

    /// <summary>The agent's key for this request: the same key returns this order rather than a second one.</summary>
    public required string IdempotencyKey { get; set; }

    /// <summary>
    /// The agent's key for the last change made to the cart (add_to_cart, remove_from_cart, create_cart on an open cart):
    /// the same change sent again finds it here and changes nothing more, so a retried "add 2" never adds 4.
    /// </summary>
    public string? LastChangeKey { get; set; }

    public PaymentOrderStatus Status { get; set; } = PaymentOrderStatus.Creating;

    /// <summary>ISO 4217, e.g. NGN.</summary>
    public required string Currency { get; set; }

    /// <summary>The amount to pay, in the currency's smallest unit (kobo): the agent's quote until Pay is pressed, then what the customer agreed to.</summary>
    public long TotalMinor { get; set; }

    /// <summary>The total the agent quoted, in kobo, kept so the checkout can show what changed.</summary>
    public long QuotedTotalMinor { get; set; }

    /// <summary>When the stock was kept aside, as the workspace's settings said when the checkout was made.</summary>
    public CheckoutHoldMode HoldMode { get; set; } = CheckoutHoldMode.AtPay;

    /// <summary>Until when the checkout link can be used to start paying.</summary>
    public DateTime? CheckoutExpiresAt { get; set; }

    /// <summary>When the customer pressed Pay and the payment link was made.</summary>
    public DateTime? PaymentStartedAt { get; set; }

    public List<PaymentOrderLine> Lines { get; set; } = [];

    /// <summary>The payment provider, e.g. accelerate.</summary>
    public required string Provider { get; set; }

    /// <summary>The provider's id for the payment session.</summary>
    public string? ProviderSessionId { get; set; }

    /// <summary>The link the customer pays at.</summary>
    public string? PaymentUrl { get; set; }

    /// <summary>Why the link couldn't be made, for the business and support. Not shown to customers.</summary>
    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the order lapses unpaid: the checkout's expiry until Pay is pressed, then the payment
    /// link's. The sweep expires it then and lets any stock kept for it go.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>When the payment arrived.</summary>
    public DateTime? PaidAt { get; set; }

    /// <summary>What the provider says was paid, in kobo.</summary>
    public long? PaidMinor { get; set; }

    /// <summary>The provider's id for the payment, so the same payment is never recorded twice.</summary>
    public string? ProviderTransactionId { get; set; }

    /// <summary>What the business should look at: a late payment, a short line, a wrong amount.</summary>
    public string? AttentionReason { get; set; }

    /// <summary>Whether a payment has been recorded for it. Once it has, no other payment or sweep changes it.</summary>
    public bool IsPaid => PaidAt is not null;
}

/// <summary>A line of the order. QuotedUnitPriceMinor is what the agent quoted; UnitPriceMinor what's being paid.</summary>
public sealed record PaymentOrderLine(
    Guid ProductId,
    Guid? VariantId,
    string Name,
    string? VariantName,
    int Quantity,
    long UnitPriceMinor,
    long LineTotalMinor,
    long? QuotedUnitPriceMinor = null)
{
    /// <summary>The agent's quote; for lines saved before quotes were kept, the price itself.</summary>
    public long QuotedUnitMinor => QuotedUnitPriceMinor ?? UnitPriceMinor;
}
