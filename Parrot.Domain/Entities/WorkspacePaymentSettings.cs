using System.Text.Json.Serialization;

namespace Parrot.Domain.Entities;

/// <summary>When the stock on a checkout is kept aside for the customer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckoutHoldMode>))]
public enum CheckoutHoldMode
{
    /// <summary>Only once the customer presses Pay, for as long as the payment link is open.</summary>
    AtPay,

    /// <summary>As soon as the agent makes the checkout, until it expires, and through the payment.</summary>
    AtCheckout,

    /// <summary>Never: stock is checked when they press Pay, but not kept aside.</summary>
    Never,
}

/// <summary>What happens when a price changed between the agent's quote and the customer pressing Pay.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PriceChangeRule>))]
public enum PriceChangeRule
{
    /// <summary>The checkout shows the old and the new price, and the customer pays the new one if they agree.</summary>
    ConfirmNewPrice,

    /// <summary>The customer can't pay: they're asked to message the business for a new checkout.</summary>
    Block,
}

/// <summary>
/// How one workspace's checkouts behave. One row per workspace, made the first time the
/// business saves; until then the platform's defaults (PaymentSettings) apply.
/// </summary>
public class WorkspacePaymentSettings
{
    /// <summary>The workspace these belong to, and the key: one row per workspace.</summary>
    public required string WorkspaceId { get; set; }

    /// <summary>How long a checkout link stays open after the agent sends it.</summary>
    public int CheckoutMinutes { get; set; }

    public CheckoutHoldMode HoldMode { get; set; }

    /// <summary>How long the payment link stays open after Pay is pressed, and the stock with it when it's held.</summary>
    public int PaymentMinutes { get; set; }

    public PriceChangeRule PriceChangeRule { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The user who last saved them.</summary>
    public string? UpdatedBy { get; set; }
}
