using System.Text.Json.Serialization;
using Parrot.Domain.Entities;

namespace Parrot.Application.Payments;

/// <summary>One thing the customer is buying.</summary>
public sealed record CheckoutItem(Guid ProductId, Guid? VariantId, int Quantity);

/// <summary>
/// What the agent asks for. There are no prices in it: they come from the catalog. AgentId is the agent as Omni
/// names it (agt_…), kept on the order as a note; it isn't looked up, as agents live in Omni.
/// </summary>
public sealed record CreateCheckoutRequest(
    string? AgentId,
    Guid ConnectionId,
    string Sender,
    IReadOnlyList<CheckoutItem> Items,
    string IdempotencyKey,
    string? CustomerName = null,
    string? CustomerEmail = null,
    string? CustomerPhone = null,
    string? Note = null);

/// <summary>
/// One item to add to or take out of a cart. Adding: Quantity is how many more (at least 1). Taking out:
/// Quantity is how many fewer, or null for all of it; VariantId can be left out when the cart has only one of the product's options.
/// </summary>
public sealed record CartItemChange(Guid ProductId, Guid? VariantId, int? Quantity);

/// <summary>A change to the conversation's cart: some items added, or some taken out. The rest of the cart stays as it is.</summary>
public sealed record ChangeCartRequest(
    string? AgentId,
    Guid ConnectionId,
    string Sender,
    IReadOnlyList<CartItemChange> Items,
    string IdempotencyKey,
    string? CustomerName = null,
    string? CustomerEmail = null,
    string? CustomerPhone = null,
    string? Note = null);

/// <summary>A line as priced, in the currency's main unit (naira).</summary>
public sealed record CheckoutLine(
    Guid ProductId,
    Guid? VariantId,
    string Name,
    string? VariantName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

/// <summary>The checkout the agent sends the customer.</summary>
public sealed record CheckoutResult(
    Guid OrderId,
    string Status,
    string CheckoutUrl,
    decimal Total,
    string Currency,
    IReadOnlyList<CheckoutLine> Lines,
    DateTime? ExpiresAt,
    [property: JsonPropertyName("for_the_agent")] string Guidance);

/// <summary>One line of a cart as the agent sees it: the ids to pass create_cart again, and today's price.</summary>
public sealed record CartLine(
    Guid ProductId,
    Guid? VariantId,
    string Name,
    string? VariantName,
    int Quantity,
    decimal QuotedUnitPrice,
    decimal? UnitPrice,
    decimal? LineTotal,
    bool PriceChanged,
    string? Problem);

/// <summary>
/// A cart as it stands, for the agent: its state (open, paying, paid, expired, replaced or unavailable),
/// its items at today's prices while it's open, and whether the customer can pay it as it is.
/// </summary>
public sealed record CartStatus(
    Guid OrderId,
    string State,
    IReadOnlyList<CartLine> Items,
    decimal Total,
    decimal QuotedTotal,
    string Currency,
    bool PriceChanged,
    bool CanPay,
    string? Problem,
    string? CheckoutUrl,
    DateTime? ExpiresAt,
    [property: JsonPropertyName("for_the_agent")] string Guidance);

/// <summary>Where a checkout is, for the customer's page.</summary>
public static class CheckoutState
{
    /// <summary>Can be paid: press Pay.</summary>
    public const string Open = "open";

    /// <summary>Pay was pressed: the payment link is open (payment_url), or being made.</summary>
    public const string Paying = "paying";

    public const string Paid = "paid";

    public const string Expired = "expired";

    /// <summary>A newer checkout for the same conversation replaced it.</summary>
    public const string Replaced = "replaced";

    /// <summary>It can't be paid: the payment couldn't be started. The business has to send a new one.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// One line on the checkout page. quoted_unit_price is what the agent quoted; unit_price is the
/// live price (null when it can't be had). problem says why it can't be paid as it stands.
/// productId and variantId name it when the customer changes its quantity; maxQuantity is the most
/// they can have while the checkout is open (null once it isn't).
/// </summary>
public sealed record CheckoutLineView(
    string Name,
    string? VariantName,
    int Quantity,
    decimal QuotedUnitPrice,
    decimal? UnitPrice,
    decimal? LineTotal,
    bool PriceChanged,
    string? Problem,
    Guid? ProductId = null,
    Guid? VariantId = null,
    int? MaxQuantity = null);

/// <summary>The customer's cart as they want it: every item they're keeping, with how many. Items left out are taken out.</summary>
public sealed record ChangeCheckoutRequest(IReadOnlyList<CheckoutItem> Items);

/// <summary>
/// The customer's checkout page. total is what Pay will charge; quoted_total what the agent said.
/// can_pay is false when an item can't be had, a price changed and the business doesn't allow
/// that, or the checkout is no longer open; message says why, in words for the customer.
/// chat_url opens the chat the checkout was sent in (wa.me, ig.me, m.me), so the customer can go back and ask for
/// something else; null when the channel has no such link.
/// </summary>
public sealed record CheckoutView(
    string State,
    string Business,
    string? LogoUrl,
    string? CustomerName,
    string Currency,
    IReadOnlyList<CheckoutLineView> Lines,
    decimal QuotedTotal,
    decimal Total,
    bool PriceChanged,
    PriceChangeRule PriceChangeRule,
    bool CanPay,
    string? Message,
    DateTime? ExpiresAt,
    string? PaymentUrl,
    string? ChatUrl = null);

/// <summary>Pressing Pay: the total the customer was shown, so a price that changed since isn't charged unseen.</summary>
public sealed record PayCheckoutRequest(decimal ExpectedTotal);

/// <summary>Where to send the customer to pay, and until when the link is open.</summary>
public sealed record PayCheckoutResult(string PaymentUrl, DateTime? ExpiresAt);

/// <summary>A checkout that can't be made or paid as asked. The message says why, in words the agent or customer can act on.</summary>
public sealed class CheckoutRefusedException(string message) : Exception(message);

/// <summary>A checkout that can't be made or paid right now: payments are off, or the provider or the catalog is down.</summary>
public sealed class CheckoutUnavailableException(string message) : Exception(message);

/// <summary>No checkout behind the link: a token that isn't ours, or an order that doesn't exist.</summary>
public sealed class CheckoutNotFoundException() : Exception("This checkout link isn't valid.");

/// <summary>Pay was refused because the checkout changed since the customer saw it. View is what it looks like now.</summary>
public sealed class CheckoutChangedException(string message, CheckoutView view) : Exception(message)
{
    public CheckoutView View { get; } = view;
}
