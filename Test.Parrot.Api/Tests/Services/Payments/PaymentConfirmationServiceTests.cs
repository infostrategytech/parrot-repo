using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Parrot.Application.Payments;
using Parrot.Domain.Entities;

namespace Test.Parrot.Api.Tests.Services.Payments;

// Payments arriving and links running out: a payment counts only from a signed webhook whose
// event id is the order's reference, for the order's exact amount, once; stock comes off when
// it's paid and goes back when it isn't; the agent is told the truth about each order.
public sealed class PaymentConfirmationServiceTests
{
    private const string Secret = "client-secret-from-accelerate";
    private const string Ws = "ws-1";

    private readonly FakePaymentOrderRepository _orders = new();
    private readonly FakeStockClient _stock = new();
    private readonly FakeNotifier _notifier = new();
    private readonly PaymentConfirmationService _service;

    public PaymentConfirmationServiceTests()
    {
        PaymentSettings settings = new() { Accelerate = new AccelerateSettings { ClientSecret = Secret } };
        _service = new PaymentConfirmationService(_orders, _stock, _notifier, CheckoutServiceTests.Links(settings, "a-secret-long-enough-for-hmac-sha256-signing!!"),
            Options.Create(settings), TimeProvider.System, NullLogger<PaymentConfirmationService>.Instance);
    }

    private PaymentOrder Order(PaymentOrderStatus status = PaymentOrderStatus.Pending, long total = 800_000, DateTime? expires = null)
    {
        PaymentOrder order = new()
        {
            WorkspaceId = Ws,
            AgentId = "agt_test",
            ConnectionId = Guid.NewGuid(),
            CustomerSender = "234801",
            Reference = "prt_" + Guid.NewGuid().ToString("N"),
            IdempotencyKey = Guid.NewGuid().ToString(),
            Status = status,
            Currency = "NGN",
            TotalMinor = total,
            Lines = [new PaymentOrderLine(Guid.NewGuid(), null, "Quilted bag", null, 2, 400_000, 800_000)],
            Provider = "accelerate",
            PaymentUrl = "https://pay.example/x",
            ExpiresAt = expires ?? DateTime.UtcNow.AddMinutes(20),
        };
        _orders.Orders.Add(order);
        return order;
    }

    private static string Sign(string eventId, string secret = Secret) =>
        Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(eventId))).ToLowerInvariant();

    private static AccelerateWebhook Paid(string reference, string amount = "8000.00", string? eventId = null, string eventName = "payment.success") =>
        new(eventName, new AccelerateWebhookData("txn-1", reference, amount, "11/16/2026 10:00:00"), "Card", eventId ?? reference, Hash: null);

    [Fact]
    public async Task A_signed_payment_for_the_full_amount_is_recorded_once_and_the_stock_comes_off()
    {
        PaymentOrder order = Order();

        WebhookOutcome first = await _service.HandleAsync(Paid(order.Reference), Sign(order.Reference), default);
        WebhookOutcome again = await _service.HandleAsync(Paid(order.Reference), Sign(order.Reference), default);

        Assert.Equal((WebhookOutcome.Recorded, WebhookOutcome.Duplicate), (first, again));
        PaymentOrder saved = _orders.Get(order.Id);
        Assert.Equal(PaymentOrderStatus.Paid, saved.Status);
        Assert.Equal((800_000L, "txn-1"), (saved.PaidMinor!.Value, saved.ProviderTransactionId));
        Assert.NotNull(saved.PaidAt);
        Assert.Null(saved.AttentionReason);
        Assert.Equal([order.Reference], _stock.Committed);
        Assert.Equal(order.Id, Assert.Single(_notifier.Told).Id);

        // The money is in the business's wallet, once, against the order's reference.
        WalletTransaction credit = Assert.Single(_orders.Credits);
        Assert.Equal((Ws, WalletTransactionType.Credit, WalletTransactionStatus.Success, WalletTransactionKind.Payment, 800_000L, 800_000L),
            (credit.WorkspaceId, credit.Type, credit.Status, credit.Kind, credit.AmountMinor, credit.BalanceAfterMinor));
        Assert.Equal(WalletTransaction.AccelerateActor, credit.ActionBy);
        Assert.Equal((order.Reference, (Guid?)order.Id, "txn-1"), (credit.Reference, credit.PaymentOrderId, credit.ProviderTransactionId));
        Assert.Equal("Payment from a customer for 2 x Quilted bag", credit.Description);
    }

    [Fact]
    public async Task Each_payment_adds_to_the_wallet_and_says_who_paid_for_what()
    {
        PaymentOrder first = Order();
        PaymentOrder second = Order(total: 150_050);
        second.CustomerName = " Ada ";
        second.Lines = [new PaymentOrderLine(Guid.NewGuid(), Guid.NewGuid(), "Zobo", "50cl", 1, 150_050, 150_050)];

        await _service.HandleAsync(Paid(first.Reference), Sign(first.Reference), default);
        await _service.HandleAsync(Paid(second.Reference, "1500.50"), Sign(second.Reference), default);

        Assert.Equal([800_000L, 950_050L], _orders.Credits.Select(c => c.BalanceAfterMinor));
        Assert.Equal("Payment from Ada for 1 x Zobo (50cl)", _orders.Credits[1].Description);
    }

    [Fact]
    public async Task The_hash_can_come_in_the_body_too()
    {
        PaymentOrder order = Order();

        WebhookOutcome outcome = await _service.HandleAsync(Paid(order.Reference) with { Hash = Sign(order.Reference) }, headerHash: null, default);

        Assert.Equal(WebhookOutcome.Recorded, outcome);
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("")]
    public async Task A_webhook_not_signed_with_the_client_secret_changes_nothing(string secret)
    {
        PaymentOrder order = Order();

        WebhookOutcome outcome = await _service.HandleAsync(Paid(order.Reference), secret.Length == 0 ? "not-hex" : Sign(order.Reference, secret), default);

        Assert.Equal(WebhookOutcome.BadSignature, outcome);
        Assert.Equal(PaymentOrderStatus.Pending, _orders.Get(order.Id).Status);
        Assert.Empty(_stock.Committed);
        Assert.Empty(_orders.Credits);
    }

    [Fact]
    public async Task A_real_webhook_pointed_at_another_order_is_ignored()
    {
        PaymentOrder paidFor = Order();
        PaymentOrder other = Order();

        // A captured webhook keeps its signed event id; swapping in another reference breaks the match.
        WebhookOutcome outcome = await _service.HandleAsync(Paid(other.Reference, eventId: paidFor.Reference), Sign(paidFor.Reference), default);

        Assert.Equal(WebhookOutcome.Ignored, outcome);
        Assert.Equal(PaymentOrderStatus.Pending, _orders.Get(other.Id).Status);
        Assert.Equal(PaymentOrderStatus.Pending, _orders.Get(paidFor.Id).Status);
    }

    [Fact]
    public async Task Other_events_and_unknown_orders_are_ignored()
    {
        PaymentOrder order = Order();

        Assert.Equal(WebhookOutcome.Ignored, await _service.HandleAsync(Paid(order.Reference, eventName: "fund.success.adept"), Sign(order.Reference), default));
        Assert.Equal(WebhookOutcome.Ignored, await _service.HandleAsync(Paid("prt_unknown"), Sign("prt_unknown"), default));
        Assert.Equal(WebhookOutcome.Ignored, await _service.HandleAsync(Paid(order.Reference, amount: "lots"), Sign(order.Reference), default));
        Assert.Equal(PaymentOrderStatus.Pending, _orders.Get(order.Id).Status);
    }

    [Theory]
    [InlineData("80.00")]
    [InlineData("8000.01")]
    public async Task The_wrong_amount_needs_a_look_and_takes_no_stock(string amount)
    {
        PaymentOrder order = Order();

        WebhookOutcome outcome = await _service.HandleAsync(Paid(order.Reference, amount), Sign(order.Reference), default);

        Assert.Equal(WebhookOutcome.Recorded, outcome);
        PaymentOrder saved = _orders.Get(order.Id);
        Assert.Equal(PaymentOrderStatus.NeedsAttention, saved.Status);
        Assert.Contains("but the order is NGN 8,000.00", saved.AttentionReason);
        Assert.Empty(_stock.Committed);
        Assert.Single(_notifier.Told);
        // The money still arrived: the wallet gets what was paid, not what was asked.
        Assert.Equal(saved.PaidMinor, Assert.Single(_orders.Credits).AmountMinor);
    }

    [Fact]
    public async Task A_payment_after_the_link_expired_is_recorded_and_says_so()
    {
        PaymentOrder order = Order(PaymentOrderStatus.Expired, expires: DateTime.UtcNow.AddMinutes(-5));

        await _service.HandleAsync(Paid(order.Reference), Sign(order.Reference), default);

        PaymentOrder saved = _orders.Get(order.Id);
        Assert.Equal(PaymentOrderStatus.Paid, saved.Status);
        Assert.Equal("Paid after the link's time ran out.", saved.AttentionReason);
        Assert.Equal([order.Reference], _stock.Committed);
    }

    [Fact]
    public async Task When_nothing_was_held_the_stock_is_found_and_taken_off_when_it_is_paid()
    {
        PaymentOrder order = Order();
        order.HoldMode = CheckoutHoldMode.Never;

        await _service.HandleAsync(Paid(order.Reference), Sign(order.Reference), default);

        Assert.Equal(PaymentOrderStatus.Paid, _orders.Get(order.Id).Status);
        var held = Assert.Single(_stock.Held);
        Assert.Equal((order.Reference, 2), (held.Reference, Assert.Single(held.Lines).Quantity));
        Assert.Equal([order.Reference], _stock.Committed);
    }

    [Fact]
    public async Task When_nothing_was_held_and_it_has_gone_it_is_paid_out_of_stock()
    {
        PaymentOrder order = Order();
        order.HoldMode = CheckoutHoldMode.Never;
        _stock.ShortWith = "Only 1 Quilted bag left.";

        await _service.HandleAsync(Paid(order.Reference), Sign(order.Reference), default);

        PaymentOrder saved = _orders.Get(order.Id);
        Assert.Equal(PaymentOrderStatus.PaidOutOfStock, saved.Status);
        Assert.Contains("Only 1 Quilted bag left.", saved.AttentionReason);
        Assert.Empty(_stock.Committed);
        Assert.Single(_notifier.Told);
    }

    [Fact]
    public async Task A_late_payment_whose_stock_went_is_paid_out_of_stock()
    {
        PaymentOrder order = Order(PaymentOrderStatus.Expired);
        _stock.CommitShort = [new StockLine(order.Lines[0].ProductId, null, 2)];

        await _service.HandleAsync(Paid(order.Reference), Sign(order.Reference), default);

        PaymentOrder saved = _orders.Get(order.Id);
        Assert.Equal(PaymentOrderStatus.PaidOutOfStock, saved.Status);
        Assert.Contains("Out of stock by then: 2 x Quilted bag.", saved.AttentionReason);
    }

    [Fact]
    public async Task When_the_catalog_is_down_nothing_is_recorded_and_accelerate_is_asked_to_send_again()
    {
        PaymentOrder order = Order();
        _stock.Down = true;

        Assert.Equal(WebhookOutcome.TryAgain, await _service.HandleAsync(Paid(order.Reference), Sign(order.Reference), default));
        Assert.Equal(PaymentOrderStatus.Pending, _orders.Get(order.Id).Status);
        Assert.Empty(_orders.Credits);

        _stock.Down = false;
        Assert.Equal(WebhookOutcome.Recorded, await _service.HandleAsync(Paid(order.Reference), Sign(order.Reference), default));
        Assert.Equal(PaymentOrderStatus.Paid, _orders.Get(order.Id).Status);
        Assert.Single(_orders.Credits);
    }

    [Fact]
    public async Task A_payment_landing_as_the_sweep_expires_the_order_is_still_recorded()
    {
        PaymentOrder order = Order();
        _orders.BeforeConditionalUpdate = stored => stored.Status = PaymentOrderStatus.Expired;

        WebhookOutcome outcome = await _service.HandleAsync(Paid(order.Reference), Sign(order.Reference), default);

        Assert.Equal(WebhookOutcome.Recorded, outcome);
        PaymentOrder saved = _orders.Get(order.Id);
        Assert.Equal(PaymentOrderStatus.Paid, saved.Status);
        Assert.Equal("Paid after the link's time ran out.", saved.AttentionReason);
    }

    [Fact]
    public async Task The_sweep_expires_orders_past_their_time_lets_their_stock_go_and_fails_stuck_ones()
    {
        PaymentOrder due = Order(expires: DateTime.UtcNow.AddMinutes(-1));
        PaymentOrder open = Order(expires: DateTime.UtcNow.AddMinutes(10));
        PaymentOrder stuck = Order(PaymentOrderStatus.Creating);
        stuck.UpdatedAt = DateTime.UtcNow.AddMinutes(-30);
        PaymentOrder unopened = Order(PaymentOrderStatus.AwaitingCheckout, expires: DateTime.UtcNow.AddMinutes(-1));
        PaymentOrder stillOpen = Order(PaymentOrderStatus.AwaitingCheckout, expires: DateTime.UtcNow.AddMinutes(10));
        PaymentOrder neverHeld = Order(PaymentOrderStatus.AwaitingCheckout, expires: DateTime.UtcNow.AddMinutes(-1));
        neverHeld.HoldMode = CheckoutHoldMode.Never;

        int expired = await _service.ExpireDueAsync(default);

        Assert.Equal(4, expired);
        Assert.Equal(PaymentOrderStatus.Expired, _orders.Get(due.Id).Status);
        Assert.Equal(PaymentOrderStatus.Pending, _orders.Get(open.Id).Status);
        Assert.Equal(PaymentOrderStatus.Failed, _orders.Get(stuck.Id).Status);
        Assert.Equal(PaymentOrderStatus.Expired, _orders.Get(unopened.Id).Status);
        Assert.Equal(PaymentOrderStatus.AwaitingCheckout, _orders.Get(stillOpen.Id).Status);
        Assert.Equal(PaymentOrderStatus.Expired, _orders.Get(neverHeld.Id).Status);
        Assert.Equal(new[] { due.Reference, stuck.Reference, unopened.Reference }.Order(), _stock.Released.Order());
    }

    [Fact]
    public async Task The_sweep_carries_on_when_the_catalog_is_down()
    {
        PaymentOrder due = Order(expires: DateTime.UtcNow.AddMinutes(-1));
        _stock.Down = true;

        Assert.Equal(1, await _service.ExpireDueAsync(default));
        Assert.Equal(PaymentOrderStatus.Expired, _orders.Get(due.Id).Status);
    }

    [Fact]
    public async Task The_agent_is_told_the_truth_about_each_order()
    {
        PaymentOrder pending = Order();
        PaymentOrder lapsed = Order(expires: DateTime.UtcNow.AddMinutes(-1));
        PaymentOrder paid = Order();
        await _service.HandleAsync(Paid(paid.Reference), Sign(paid.Reference), default);

        PaymentStatusResult waiting = await _service.GetStatusAsync(Ws, pending.Id, default);
        Assert.Equal(("awaiting_payment", "https://pay.example/x"), (waiting.Status, waiting.PaymentUrl));
        Assert.Contains("Don't say it's paid", waiting.Guidance);

        PaymentOrder unopened = Order(PaymentOrderStatus.AwaitingCheckout, expires: DateTime.UtcNow.AddMinutes(10));
        unopened.CheckoutExpiresAt = unopened.ExpiresAt;
        PaymentStatusResult notYet = await _service.GetStatusAsync(Ws, unopened.Id, default);
        Assert.Equal(("awaiting_payment", (string?)null), (notYet.Status, notYet.PaymentUrl));
        Assert.StartsWith("https://app.example/checkout/", notYet.CheckoutUrl);
        Assert.Null(waiting.CheckoutUrl);

        PaymentStatusResult ranOut = await _service.GetStatusAsync(Ws, lapsed.Id, default);
        Assert.Equal(("expired", (string?)null), (ranOut.Status, ranOut.PaymentUrl));

        PaymentStatusResult done = await _service.GetStatusAsync(Ws, paid.Id, default);
        Assert.Equal("paid", done.Status);
        Assert.NotNull(done.PaidAt);

        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.GetStatusAsync("ws-2", pending.Id, default));

        // By reference too, but only the business's own.
        Assert.Equal(pending.Id, (await _service.GetStatusAsync(Ws, " " + pending.Reference + " ", default)).OrderId);
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.GetStatusAsync("ws-2", pending.Reference, default));
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.GetStatusAsync(Ws, "not-an-order", default));
        Assert.Equal(pending.Id, (await _service.GetStatusAsync(Ws, $"{{\"order_id\": \"{pending.Id}\"}}", default)).OrderId);
        Assert.Equal(pending.Id, (await _service.GetStatusAsync(Ws, $"\"{pending.Reference}\"", default)).OrderId);
    }

    [Theory]
    [InlineData("1000.00", 100_000)]
    [InlineData("1000", 100_000)]
    [InlineData("0.5", 50)]
    public void Accelerates_naira_amount_becomes_kobo(string amount, long kobo)
    {
        Assert.True(PaymentConfirmationService.TryKobo(amount, out long parsed));
        Assert.Equal(kobo, parsed);
    }
}
