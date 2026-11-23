using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.Payments;
using Test.Parrot.Api.Tests.Services.Integrations;

namespace Test.Parrot.Api.Tests.Services.Payments;

// Payment orders against a real PostgreSQL: one order per idempotency key even when the same
// request arrives twice at once, lines kept as they were priced, and a new link cancelling
// only the same conversation's open ones. Run against a disposable database: see PARROT_TEST_POSTGRES.
public sealed class PaymentOrderRepositoryPostgresTests : IAsyncLifetime
{
    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private DbContextOptions<ApplicationDbContext>? _options;

    public async Task InitializeAsync()
    {
        string? server = Environment.GetEnvironmentVariable("PARROT_TEST_POSTGRES");
        if (server is null) return;
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(server) { Database = _database }.ConnectionString).Options;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner@example.com", Email = "owner@example.com" });
        db.Workspaces.AddRange(new Workspace { Id = "ws-1", OwnerId = "owner" }, new Workspace { Id = "ws-2", OwnerId = "owner" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private EfPaymentOrderRepository Repository() => new(new Contexts(_options!));

    private sealed class Contexts(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private static PaymentOrder Order(string workspace, string key, Guid connection, string sender = "234801", PaymentOrderStatus status = PaymentOrderStatus.Pending) => new()
    {
        WorkspaceId = workspace,
        AgentId = Guid.NewGuid(),
        ConnectionId = connection,
        CustomerSender = sender,
        Reference = "prt_" + Guid.NewGuid().ToString("N"),
        IdempotencyKey = key,
        Status = status,
        Currency = "NGN",
        TotalMinor = 800_000,
        Lines = [new PaymentOrderLine(Guid.NewGuid(), null, "Quilted bag", null, 2, 400_000, 800_000)],
        Provider = "accelerate",
    };

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task The_same_key_saved_twice_at_once_keeps_one_order_per_workspace()
    {
        EfPaymentOrderRepository repository = Repository();
        Guid connection = Guid.NewGuid();

        bool[] saved = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => repository.TryAddAsync(Order("ws-1", "key-1", connection), default)));

        Assert.Equal(1, saved.Count(s => s));
        Assert.True(await repository.TryAddAsync(Order("ws-2", "key-1", connection), default));
        PaymentOrder found = (await repository.FindByIdempotencyKeyAsync("ws-1", "key-1", default))!;
        Assert.Equal(("Quilted bag", 2, 800_000L), (found.Lines[0].Name, found.Lines[0].Quantity, found.Lines[0].LineTotalMinor));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task A_new_link_cancels_only_the_same_conversations_open_orders()
    {
        EfPaymentOrderRepository repository = Repository();
        Guid connection = Guid.NewGuid();
        PaymentOrder older = Order("ws-1", "key-1", connection);
        PaymentOrder failed = Order("ws-1", "key-2", connection, status: PaymentOrderStatus.Failed);
        PaymentOrder otherCustomer = Order("ws-1", "key-3", connection, sender: "234802");
        PaymentOrder otherWorkspace = Order("ws-2", "key-4", connection);
        PaymentOrder newest = Order("ws-1", "key-5", connection);
        foreach (PaymentOrder order in new[] { older, failed, otherCustomer, otherWorkspace, newest })
            Assert.True(await repository.TryAddAsync(order, default));

        IReadOnlyList<PaymentOrder> cancelled = await repository.CancelOpenAsync("ws-1", connection, "234801", newest.Id, DateTime.UtcNow, default);

        Assert.Equal(older.Reference, Assert.Single(cancelled).Reference);
        Assert.Equal(PaymentOrderStatus.Cancelled, (await repository.FindByIdempotencyKeyAsync("ws-1", "key-1", default))!.Status);
        Assert.Equal(PaymentOrderStatus.Failed, (await repository.FindByIdempotencyKeyAsync("ws-1", "key-2", default))!.Status);
        Assert.Equal(PaymentOrderStatus.Pending, (await repository.FindByIdempotencyKeyAsync("ws-1", "key-3", default))!.Status);
        Assert.Equal(PaymentOrderStatus.Pending, (await repository.FindByIdempotencyKeyAsync("ws-2", "key-4", default))!.Status);
        Assert.Equal(PaymentOrderStatus.Pending, (await repository.FindByIdempotencyKeyAsync("ws-1", "key-5", default))!.Status);
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task The_open_cart_is_the_conversations_unpaid_one_still_in_time()
    {
        EfPaymentOrderRepository repository = Repository();
        Guid connection = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        PaymentOrder open = Order("ws-1", "open", connection, status: PaymentOrderStatus.AwaitingCheckout);
        open.ExpiresAt = now.AddMinutes(10);
        PaymentOrder lapsed = Order("ws-1", "lapsed", connection, status: PaymentOrderStatus.AwaitingCheckout);
        lapsed.ExpiresAt = now.AddMinutes(-1);
        PaymentOrder paying = Order("ws-1", "paying", connection, status: PaymentOrderStatus.Pending);
        paying.ExpiresAt = now.AddMinutes(10);
        PaymentOrder someoneElse = Order("ws-1", "other", connection, sender: "234899", status: PaymentOrderStatus.AwaitingCheckout);
        someoneElse.ExpiresAt = now.AddMinutes(10);
        foreach (PaymentOrder order in new[] { open, lapsed, paying, someoneElse }) await repository.TryAddAsync(order, default);

        Assert.Equal(open.Id, (await repository.FindOpenCartAsync("ws-1", connection, "234801", now, default))?.Id);
        Assert.Null(await repository.FindOpenCartAsync("ws-2", connection, "234801", now, default));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task A_cart_change_saves_only_over_the_cart_as_it_was_read()
    {
        EfPaymentOrderRepository repository = Repository();
        Guid connection = Guid.NewGuid();
        PaymentOrder cart = Order("ws-1", "key-1", connection, status: PaymentOrderStatus.AwaitingCheckout);
        cart.ExpiresAt = DateTime.UtcNow.AddMinutes(10);
        await repository.TryAddAsync(cart, default);

        // Two changes from the same read, at once: the database's own timestamp is what they're checked against.
        PaymentOrder read = (await repository.FindOpenCartAsync("ws-1", connection, "234801", DateTime.UtcNow, default))!;
        async Task<PaymentOrder> Changed(int quantity, string reference)
        {
            PaymentOrder change = (await repository.FindAsync("ws-1", cart.Id, default))!;
            change.Lines = [new PaymentOrderLine(Guid.NewGuid(), null, "Quilted bag", null, quantity, 400_000, 400_000L * quantity)];
            change.TotalMinor = change.QuotedTotalMinor = 400_000L * quantity;
            change.Reference = reference;
            change.Note = "Deliver to Lekki";
            change.UpdatedAt = DateTime.UtcNow.AddTicks(quantity);
            return change;
        }

        bool[] saved = await Task.WhenAll(Changed(3, "prt_three"), Changed(5, "prt_five")).ContinueWith(t =>
            Task.WhenAll(t.Result.Select(c => repository.TryReplaceCartAsync(c, read.UpdatedAt, default)))).Unwrap();

        Assert.Equal(1, saved.Count(s => s));
        PaymentOrder after = (await repository.FindAsync("ws-1", cart.Id, default))!;
        Assert.Contains(after.Lines[0].Quantity, new[] { 3, 5 });
        Assert.Equal((after.Lines[0].Quantity == 3 ? "prt_three" : "prt_five", "Deliver to Lekki"), (after.Reference, after.Note));

        // Once Pay is pressed, it can't be changed.
        after.Status = PaymentOrderStatus.Creating;
        Assert.True(await repository.TryMoveAsync(after, PaymentOrderStatus.AwaitingCheckout, default));
        PaymentOrder paying = (await repository.FindAsync("ws-1", cart.Id, default))!;
        paying.Status = PaymentOrderStatus.AwaitingCheckout;
        Assert.False(await repository.TryReplaceCartAsync(paying, paying.UpdatedAt, default));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task An_update_saves_the_link()
    {
        EfPaymentOrderRepository repository = Repository();
        PaymentOrder order = Order("ws-1", "key-1", Guid.NewGuid(), status: PaymentOrderStatus.Creating);
        await repository.TryAddAsync(order, default);

        order.Status = PaymentOrderStatus.Pending;
        order.PaymentUrl = "https://pay.example/s/1";
        order.ProviderSessionId = "sess-1";
        await repository.UpdateAsync(order, default);

        PaymentOrder found = (await repository.FindByIdempotencyKeyAsync("ws-1", "key-1", default))!;
        Assert.Equal((PaymentOrderStatus.Pending, "https://pay.example/s/1", "sess-1"), (found.Status, found.PaymentUrl, found.ProviderSessionId));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task A_payment_and_the_sweep_reaching_one_order_at_once_only_one_wins()
    {
        EfPaymentOrderRepository repository = Repository();
        PaymentOrder order = Order("ws-1", "key-1", Guid.NewGuid());
        await repository.TryAddAsync(order, default);

        PaymentOrder paid = (await repository.FindByReferenceAsync(order.Reference, default))!;
        paid.Status = PaymentOrderStatus.Paid;
        paid.PaidAt = DateTime.UtcNow;
        paid.PaidMinor = 800_000;
        PaymentOrder expired = (await repository.FindByReferenceAsync(order.Reference, default))!;
        expired.Status = PaymentOrderStatus.Expired;

        Assert.True(await repository.TryUpdateFromAsync(paid, PaymentOrderStatus.Pending, default));
        Assert.False(await repository.TryUpdateFromAsync(expired, PaymentOrderStatus.Pending, default));
        PaymentOrder found = (await repository.FindAsync("ws-1", order.Id, default))!;
        Assert.Equal((PaymentOrderStatus.Paid, 800_000L), (found.Status, found.PaidMinor!.Value));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task The_sweep_finds_pending_orders_past_their_time_and_orders_stuck_being_made()
    {
        EfPaymentOrderRepository repository = Repository();
        DateTime now = DateTime.UtcNow;
        PaymentOrder due = Order("ws-1", "key-1", Guid.NewGuid());
        due.ExpiresAt = now.AddMinutes(-1);
        PaymentOrder notYet = Order("ws-1", "key-2", Guid.NewGuid());
        notYet.ExpiresAt = now.AddMinutes(10);
        PaymentOrder stuck = Order("ws-1", "key-3", Guid.NewGuid(), status: PaymentOrderStatus.Creating);
        stuck.UpdatedAt = now.AddMinutes(-30);
        PaymentOrder unopened = Order("ws-1", "key-5", Guid.NewGuid(), status: PaymentOrderStatus.AwaitingCheckout);
        unopened.ExpiresAt = now.AddMinutes(-1);
        PaymentOrder paid = Order("ws-2", "key-4", Guid.NewGuid());
        paid.ExpiresAt = now.AddMinutes(-1);
        paid.PaidAt = now;
        foreach (PaymentOrder o in new[] { due, notYet, stuck, paid, unopened })
            await repository.TryAddAsync(o, default);

        IReadOnlyList<PaymentOrder> found = await repository.ListDueAsync(now, now.AddMinutes(-10), 100, default);

        Assert.Equal(new[] { due.Id, stuck.Id, unopened.Id }.Order(), found.Select(o => o.Id).Order());
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task Pressing_pay_twice_at_once_starts_one_payment_with_the_new_prices_saved()
    {
        EfPaymentOrderRepository repository = Repository();
        PaymentOrder order = Order("ws-1", "key-1", Guid.NewGuid(), status: PaymentOrderStatus.AwaitingCheckout);
        order.QuotedTotalMinor = 800_000;
        order.HoldMode = CheckoutHoldMode.AtCheckout;
        await repository.TryAddAsync(order, default);

        PaymentOrder starting = (await repository.FindAsync("ws-1", order.Id, default))!;
        starting.Status = PaymentOrderStatus.Creating;
        starting.Lines = [new PaymentOrderLine(order.Lines[0].ProductId, null, "Quilted bag", null, 2, 450_000, 900_000, 400_000)];
        starting.TotalMinor = 900_000;

        bool[] moved = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => repository.TryMoveAsync(starting, PaymentOrderStatus.AwaitingCheckout, default)));

        Assert.Equal(1, moved.Count(m => m));
        PaymentOrder found = (await repository.FindAsync("ws-1", order.Id, default))!;
        Assert.Equal((PaymentOrderStatus.Creating, 900_000L, 800_000L, CheckoutHoldMode.AtCheckout), (found.Status, found.TotalMinor, found.QuotedTotalMinor, found.HoldMode));
        Assert.Equal((450_000L, 400_000L), (found.Lines[0].UnitPriceMinor, found.Lines[0].QuotedUnitMinor));

        // Another workspace can't move it, even by id.
        starting.WorkspaceId = "ws-2";
        Assert.False(await repository.TryMoveAsync(starting, PaymentOrderStatus.Creating, default));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task Each_workspace_keeps_its_own_checkout_settings()
    {
        EfWorkspacePaymentSettingsRepository settings = new(new Contexts(_options!));

        await settings.SaveAsync(new WorkspacePaymentSettings { WorkspaceId = "ws-1", CheckoutMinutes = 60, HoldMode = CheckoutHoldMode.Never, PaymentMinutes = 20, PriceChangeRule = PriceChangeRule.Block }, default);
        await settings.SaveAsync(new WorkspacePaymentSettings { WorkspaceId = "ws-1", CheckoutMinutes = 90, HoldMode = CheckoutHoldMode.AtCheckout, PaymentMinutes = 20, PriceChangeRule = PriceChangeRule.Block }, default);

        WorkspacePaymentSettings mine = (await settings.GetAsync("ws-1", default))!;
        Assert.Equal((90, CheckoutHoldMode.AtCheckout), (mine.CheckoutMinutes, mine.HoldMode));
        Assert.Null(await settings.GetAsync("ws-2", default));

        // The database refuses minutes out of range even if the service were bypassed.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => settings.SaveAsync(new WorkspacePaymentSettings { WorkspaceId = "ws-2", CheckoutMinutes = 2, PaymentMinutes = 20 }, default));
    }
}
