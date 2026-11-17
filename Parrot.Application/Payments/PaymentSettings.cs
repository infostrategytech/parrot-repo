namespace Parrot.Application.Payments;

/// <summary>
/// Checkouts and payment links. One payment account is used for every business: the platform's
/// own Accelerate account, so its keys are server settings and never a business's. The minutes,
/// hold and price rule here are defaults: each workspace can change its own (WorkspacePaymentSettings).
/// </summary>
public sealed class PaymentSettings
{
    public const string SectionName = "Payments";

    /// <summary>The most one link may ask for, in the currency's main unit (naira).</summary>
    public decimal MaxLinkAmount { get; set; } = 1_000_000m;

    /// <summary>Default: how long a checkout link stays open.</summary>
    public int CheckoutMinutes { get; set; } = 30;

    /// <summary>Default: how long the payment link (and any stock held for it) stays open after Pay is pressed.</summary>
    public int LinkMinutes { get; set; } = 15;

    /// <summary>Default: when stock is kept aside.</summary>
    public Parrot.Domain.Entities.CheckoutHoldMode HoldMode { get; set; } = Parrot.Domain.Entities.CheckoutHoldMode.AtPay;

    /// <summary>Default: what a price change between quote and Pay does.</summary>
    public Parrot.Domain.Entities.PriceChangeRule PriceChangeRule { get; set; } = Parrot.Domain.Entities.PriceChangeRule.ConfirmNewPrice;

    /// <summary>
    /// The web app the checkout page is served from (…/checkout/{token}). The email settings'
    /// BaseUrl, where invite links point, is used when this is empty.
    /// </summary>
    public string? CheckoutBaseUrl { get; set; }

    /// <summary>Where the customer lands after paying. A public page.</summary>
    public string? CallbackUrl { get; set; }

    public AccelerateSettings Accelerate { get; set; } = new ();
}

/// <summary>The platform's Accelerate account. The keys come from the environment, never from a committed file.</summary>
public sealed class AccelerateSettings
{
    /// <summary>The payments API; the sandbox unless set.</summary>
    public string BaseUrl { get; set; } = "https://staging.payment.irechargetech.com";

    public string? PublicKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Signs Accelerate's webhooks (HMAC-SHA512 of the event id). Without it no payment is accepted.</summary>
    public string? ClientSecret { get; set; }
}
