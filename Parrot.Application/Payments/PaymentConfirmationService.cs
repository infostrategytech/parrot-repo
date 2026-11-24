using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Domain.Entities;

namespace Parrot.Application.Payments;

/// <summary>What Accelerate sends when a payment link is paid.</summary>
public sealed record AccelerateWebhook(
    [property: JsonPropertyName("eventName")] string? EventName,
    [property: JsonPropertyName("data")] AccelerateWebhookData? Data,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("eventId")] string? EventId,
    [property: JsonPropertyName("hash")] string? Hash);

public sealed record AccelerateWebhookData(
    [property: JsonPropertyName("transactionId")] string? TransactionId,
    [property: JsonPropertyName("merchantReference")] string? MerchantReference,
    [property: JsonPropertyName("amount")] string? Amount,
    [property: JsonPropertyName("date")] string? Date);

/// <summary>What became of a webhook, which decides what Accelerate is answered.</summary>
public enum WebhookOutcome
{
    /// <summary>The payment was recorded.</summary>
    Recorded,

    /// <summary>Already recorded: a resend.</summary>
    Duplicate,

    /// <summary>Not for us, or not about an order we know. Answered 200 so it isn't resent; logged.</summary>
    Ignored,

    /// <summary>The signature doesn't match: not from Accelerate. Answered 401.</summary>
    BadSignature,

    /// <summary>Couldn't be recorded now (the catalog is down). Answered 503, so Accelerate sends it again.</summary>
    TryAgain,
}

/// <summary>An order as the agent may describe it to the customer.</summary>
public sealed record PaymentStatusResult(
    Guid OrderId,
    string Status,
    decimal Total,
    string Currency,
    string? PaymentUrl,
    string? CheckoutUrl,
    DateTime? ExpiresAt,
    DateTime? PaidAt,
    [property: JsonPropertyName("for_the_agent")] string Guidance);

/// <summary>Tells the business about a paid order, or one that needs a look.</summary>
public interface IPaymentNotifier
{
    Task PaidAsync(PaymentOrder order, CancellationToken ct);
}

/// <summary>
/// Records payments and lets unpaid orders go. A payment counts only from a webhook signed with
/// the platform's client secret, whose event id is the order's reference, for the order's exact
/// amount. The signature covers the event id alone, so tying it to the reference is what stops
/// a real webhook being replayed against another order; the amount check stops one for less.
/// </summary>
public sealed partial class PaymentConfirmationService(
    IPaymentOrderRepository orders,
    ICatalogStockClient stock,
    IPaymentNotifier notifier,
    CheckoutLinks links,
    IOptions<PaymentSettings> settings,
    TimeProvider clock,
    ILogger<PaymentConfirmationService> logger)
{
    public const string PaymentSuccess = "payment.success";

    /// <summary>How long an order may stay half made (saved, link not yet stored) before the sweep fails it.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Whether <paramref name="hash"/> is Accelerate's HMAC-SHA512 of the event id with the client secret.</summary>
    public bool IsSigned(string? eventId, string? hash)
    {
        string? secret = settings.Value.Accelerate.ClientSecret;
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(eventId) || string.IsNullOrEmpty(hash))
        {
            return false;
        }

        byte[] expected = HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(eventId));
        byte[] given;
        try
        {
            given = Convert.FromHexString(hash.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public async Task<WebhookOutcome> HandleAsync(AccelerateWebhook webhook, string? headerHash, CancellationToken ct)
    {
        if (!IsSigned(webhook.EventId, headerHash ?? webhook.Hash))
        {
            logger.LogWarning("Accelerate webhook refused: bad signature. EventId={EventId}", webhook.EventId);
            return WebhookOutcome.BadSignature;
        }

        if (webhook.EventName != PaymentSuccess)
        {
            logger.LogInformation("Accelerate webhook ignored: event {EventName}. EventId={EventId}", webhook.EventName, webhook.EventId);
            return WebhookOutcome.Ignored;
        }

        string? reference = webhook.Data?.MerchantReference;
        if (string.IsNullOrEmpty(reference) || reference != webhook.EventId)
        {
            logger.LogWarning("Accelerate webhook ignored: its event id isn't its reference. EventId={EventId} Reference={Reference}", webhook.EventId, reference);
            return WebhookOutcome.Ignored;
        }

        if (!TryKobo(webhook.Data!.Amount, out long paid))
        {
            logger.LogWarning("Accelerate webhook ignored: unreadable amount {Amount}. Reference={Reference}", webhook.Data.Amount, reference);
            return WebhookOutcome.Ignored;
        }

        // The sweep may expire the order while this runs: whoever saves first wins, and the other tries again.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            PaymentOrder? order = await orders.FindByReferenceAsync(reference, ct);
            if (order is null)
            {
                logger.LogWarning("Accelerate webhook ignored: no order {Reference}.", reference);
                return WebhookOutcome.Ignored;
            }

            if (order.IsPaid)
            {
                return WebhookOutcome.Duplicate;
            }

            PaymentOrderStatus before = order.Status;
            order.PaidAt = Now;
            order.PaidMinor = paid;
            order.ProviderTransactionId = webhook.Data.TransactionId;
            order.UpdatedAt = Now;

            if (paid != order.TotalMinor)
            {
                // Paid, but not what was asked: the business decides. The stock stays as it is.
                order.Status = PaymentOrderStatus.NeedsAttention;
                order.AttentionReason = $"The customer paid {Naira(paid)}, but the order is {Naira(order.TotalMinor)}.";
            }
            else
            {
                IReadOnlyList<StockLine> shortLines;
                string? notHeld = null;
                try
                {
                    // Nothing was kept aside for this order (the workspace holds no stock), so its stock is found
                    // now and taken off with the rest. A retried webhook finds the same hold, never a second one.
                    if (order.HoldMode == CheckoutHoldMode.Never)
                    {
                        try
                        {
                            await stock.HoldAsync(order.WorkspaceId, order.Reference, Now.AddMinutes(10),
                                order.Lines.Select(l => new StockLine(l.ProductId, l.VariantId, l.Quantity)).ToArray(), ct);
                        }
                        catch (StockShortException ex)
                        {
                            notHeld = ex.Message;
                        }
                    }

                    shortLines = notHeld is null ? await stock.CommitAsync(order.WorkspaceId, order.Reference, ct) : [];
                }
                catch (CatalogStockUnavailableException ex)
                {
                    logger.LogError("Payment not recorded yet: the catalog can't take the stock off. Order={Order} Reason={Reason}", order.Id, ex.Message);
                    return WebhookOutcome.TryAgain;
                }

                string? late = before switch
                {
                    PaymentOrderStatus.Pending => null,
                    PaymentOrderStatus.Expired => "Paid after the link's time ran out.",
                    PaymentOrderStatus.Cancelled => "Paid on an older link that a newer one had replaced.",
                    _ => $"Paid while the order was {before}.",
                };

                if (notHeld is not null)
                {
                    order.Status = PaymentOrderStatus.PaidOutOfStock;
                    order.AttentionReason = $"{late} Paid, but not enough was left: {notHeld} Refund or arrange it with the customer.".Trim();
                }
                else if (shortLines.Count > 0)
                {
                    order.Status = PaymentOrderStatus.PaidOutOfStock;
                    string missing = string.Join(", ", shortLines.Select(s => $"{s.Quantity} x {NameOf(order, s)}"));
                    order.AttentionReason = $"{late} Out of stock by then: {missing}. Refund or arrange it with the customer.".Trim();
                }
                else
                {
                    order.Status = PaymentOrderStatus.Paid;
                    order.AttentionReason = late;
                }
            }

            // Whatever the order's state, the money arrived: the business's wallet gets what was paid.
            WalletTransaction credit = CreditFor(order, paid);
            if (await orders.TryRecordPaymentAsync(order, before, credit, ct))
            {
                logger.LogInformation("Payment recorded. Workspace={Workspace} Order={Order} Status={Status} PaidMinor={Paid} Transaction={Transaction} WalletBalanceMinor={Balance}",
                    order.WorkspaceId, order.Id, order.Status, paid, order.ProviderTransactionId, credit.BalanceAfterMinor);
                await NotifyQuietlyAsync(order);
                return WebhookOutcome.Recorded;
            }
        }

        logger.LogError("Payment not recorded: the order kept changing. Reference={Reference}", reference);
        return WebhookOutcome.TryAgain;
    }

    /// <summary>Marks open orders (checkout or payment) whose time is up as expired and lets their stock go; fails orders stuck half made.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        IReadOnlyList<PaymentOrder> due = await orders.ListDueAsync(Now, Now - StuckAfter, 100, ct);
        int expired = 0;
        foreach (PaymentOrder order in due)
        {
            PaymentOrderStatus before = order.Status;
            order.Status = before is PaymentOrderStatus.Pending or PaymentOrderStatus.AwaitingCheckout ? PaymentOrderStatus.Expired : PaymentOrderStatus.Failed;
            order.FailureReason = before == PaymentOrderStatus.Creating ? "The payment link was never finished." : order.FailureReason;
            order.UpdatedAt = Now;
            if (!await orders.TryUpdateFromAsync(order, before, ct))
            {
                continue;
            }

            expired++;
            if (order.HoldMode == CheckoutHoldMode.Never)
            {
                continue;
            }

            try
            {
                await stock.ReleaseAsync(order.WorkspaceId, order.Reference, ct);
            }
            catch (CatalogStockUnavailableException ex)
            {
                // The hold runs out by itself; releasing it only tidies up.
                logger.LogWarning("Stock hold not released; it runs out at its time anyway. Order={Order} Reason={Reason}", order.Id, ex.Message);
            }
        }

        if (expired > 0)
        {
            logger.LogInformation("Payment orders expired: {Count}.", expired);
        }

        return expired;
    }

    public Task<PaymentStatusResult> GetStatusAsync(string workspaceId, Guid orderId, CancellationToken ct) =>
        GetStatusAsync(workspaceId, orderId.ToString(), ct);

    /// <summary>The order's state, found by its order_id or by its prt_ reference.</summary>
    public async Task<PaymentStatusResult> GetStatusAsync(string workspaceId, string orderIdOrReference, CancellationToken ct)
    {
        // Agents and clients wrap the id in quotes or JSON, so the id is picked out of the text.
        Match key = OrderKey().Match(orderIdOrReference);
        PaymentOrder? found = !key.Success ? null
            : key.Groups["id"].Success ? await orders.FindAsync(workspaceId, Guid.Parse(key.Groups["id"].Value), ct)
            : await orders.FindByReferenceAsync(key.Groups["ref"].Value, ct) is { } byReference && byReference.WorkspaceId == workspaceId ? byReference
            : null;
        if (found is null)
        {
            logger.LogInformation("Payment status asked for an unknown order. Workspace={Workspace} Given={Given}",
                workspaceId, orderIdOrReference.Length > 120 ? orderIdOrReference[..120] : orderIdOrReference);
        }

        PaymentOrder order = found
            ?? throw new CheckoutRefusedException("There's no such order for this business. Pass the order_id create_cart returned (a UUID such as 70c1f69c-3815-40b6-ab11-2ce6b7eb6577).");

        // A checkout or link whose time is up reads as expired straight away, even before the sweep marks it.
        PaymentOrderStatus status = order.Status is PaymentOrderStatus.Pending or PaymentOrderStatus.AwaitingCheckout && order.ExpiresAt <= Now ? PaymentOrderStatus.Expired : order.Status;
        (string word, string guidance) = status switch
        {
            // Before or after they press Pay on the checkout, the agent's answer is the same: not paid yet.
            PaymentOrderStatus.AwaitingCheckout => ("awaiting_payment", "No payment has arrived yet. If the customer lost the link, send checkout_url again. Don't say it's paid."),
            PaymentOrderStatus.Creating => ("awaiting_payment", "The customer pressed Pay and the payment page is opening. Don't say it's paid."),
            PaymentOrderStatus.Pending => ("awaiting_payment", "No payment has arrived yet. If the customer says they've paid, tell them it can take a few minutes to come through and that the business will confirm. Don't say it's paid."),
            PaymentOrderStatus.Paid => ("paid", "The payment arrived. Thank the customer; the business will be in touch about delivery."),
            PaymentOrderStatus.PaidOutOfStock => ("paid_needs_attention", "The payment arrived, but some items had sold out by then. Tell the customer the business will contact them to sort it out. Don't promise a refund or delivery."),
            PaymentOrderStatus.NeedsAttention => ("paid_needs_attention", "A payment arrived that doesn't match the order. Tell the customer the business will check it and get back to them."),
            PaymentOrderStatus.Expired => ("expired", "The checkout's time ran out unpaid and the items were let go. If the customer still wants them, check get_prices and call create_cart again."),
            PaymentOrderStatus.Cancelled => ("replaced", "A newer cart replaced this one. Send the newer checkout link."),
            _ => ("not_sent", "This checkout couldn't be made or paid. Make a new one if the customer still wants the items."),
        };

        return new PaymentStatusResult(order.Id, word, CheckoutService.FromMinor(order.TotalMinor), order.Currency,
            status == PaymentOrderStatus.Pending ? order.PaymentUrl : null,
            status == PaymentOrderStatus.AwaitingCheckout && links.IsConfigured ? links.For(order) : null,
            order.ExpiresAt, order.PaidAt, guidance);
    }

    private async Task NotifyQuietlyAsync(PaymentOrder order)
    {
        try
        {
            await notifier.PaidAsync(order, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The business wasn't told about a payment. Order={Order}", order.Id);
        }
    }

    // An order id (UUID) or a reference (prt_ and 32 hex), anywhere in the text.
    [GeneratedRegex(@"(?<id>\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b)|(?<ref>\bprt_[0-9a-f]{32}\b)")]
    private static partial Regex OrderKey();

    /// <summary>Accelerate's naira amount ("1000.00") in kobo.</summary>
    public static bool TryKobo(string? amount, out long kobo)
    {
        kobo = 0;
        if (!decimal.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal naira) || naira <= 0)
        {
            return false;
        }

        kobo = CheckoutService.ToMinor(naira);
        return true;
    }

    /// <summary>The wallet credit for a payment: what was paid, against the order's reference, so it's only ever counted once.</summary>
    internal WalletTransaction CreditFor(PaymentOrder order, long paidMinor)
    {
        string items = string.Join(", ", order.Lines.Select(l => $"{l.Quantity} x {(l.VariantName is null ? l.Name : $"{l.Name} ({l.VariantName})")}"));
        string from = string.IsNullOrWhiteSpace(order.CustomerName) ? "a customer" : order.CustomerName.Trim();
        string description = items.Length == 0 ? $"Payment from {from}" : $"Payment from {from} for {items}";
        return new WalletTransaction
        {
            WorkspaceId = order.WorkspaceId,
            Type = WalletTransactionType.Credit,
            Status = WalletTransactionStatus.Success,
            Kind = WalletTransactionKind.Payment,
            ActionBy = WalletTransaction.AccelerateActor,
            AmountMinor = paidMinor,
            Currency = order.Currency,
            Reference = order.Reference,
            PaymentOrderId = order.Id,
            ProviderTransactionId = order.ProviderTransactionId,
            Description = description.Length > 500 ? description[..497] + "..." : description,
            CreatedAt = Now,
        };
    }

    private static string Naira(long kobo) => $"NGN {CheckoutService.FromMinor(kobo):N2}";

    private static string NameOf(PaymentOrder order, StockLine line)
    {
        PaymentOrderLine? match = order.Lines.FirstOrDefault(l => l.ProductId == line.ProductId && l.VariantId == line.VariantId);
        return match is null ? line.ProductId.ToString() : match.VariantName is null ? match.Name : $"{match.Name} ({match.VariantName})";
    }
}
