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

    /// <summary>
    /// Default: how long the payment link (and any stock held for it) stays open after Pay is pressed. Accelerate's
    /// links never expire on their side, so this is the only limit; it's generous enough for a customer to finish.
    /// </summary>
    public int LinkMinutes { get; set; } = 30;

    /// <summary>Default: when stock is kept aside.</summary>
    public Parrot.Domain.Entities.CheckoutHoldMode HoldMode { get; set; } = Parrot.Domain.Entities.CheckoutHoldMode.AtPay;

    /// <summary>Default: what a price change between quote and Pay does.</summary>
    public Parrot.Domain.Entities.PriceChangeRule PriceChangeRule { get; set; } = Parrot.Domain.Entities.PriceChangeRule.ConfirmNewPrice;

    /// <summary>
    /// The web app the checkout page is served from (…/checkout/{token}). The email settings'
    /// BaseUrl, where invite links point, is used when this is empty.
    /// </summary>
    public string? CheckoutBaseUrl { get; set; }

    /// <summary>
    /// Where the customer lands after paying when there's no checkout to send them back to. Checkouts send the
    /// customer back to their own checkout page, so this is only a fallback.
    /// </summary>
    public string? CallbackUrl { get; set; }

    public AccelerateSettings Accelerate { get; set; } = new ();
}

/// <summary>The platform's Accelerate account. The keys come from the environment, never from a committed file.</summary>
public sealed class AccelerateSettings
{
    /// <summary>The payments API: production unless set (the sandbox is https://staging.payment.irechargetech.com).</summary>
    public string BaseUrl { get; set; } = "https://prod.payment.irechargetech.com";

    /// <summary>Where the keys are exchanged for a token (GET auth/api-client/token): a separate user-management API, not the payments one.</summary>
    public string AuthBaseUrl { get; set; } = "https://prod.user-mgt.irechargetech.com/api/v1";

    public string? PublicKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Signs Accelerate's webhooks (HMAC-SHA512 of the event id). Without it no payment is accepted.</summary>
    public string? ClientSecret { get; set; }
}
