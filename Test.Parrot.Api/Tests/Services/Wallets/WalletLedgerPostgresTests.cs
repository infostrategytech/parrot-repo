using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Models;
using Parrot.Application.Wallets;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.Payments;
using Parrot.Infrastructure.Wallets;
using Test.Parrot.Api.Tests.Services.Integrations;

namespace Test.Parrot.Api.Tests.Services.Wallets;

// The wallet's ledger against a real PostgreSQL: a payment and its credit are saved together or not at all, each
// payment is counted once however often it arrives, and each entry's balance-after is right even when they race.
// Run against a disposable database: see PARROT_TEST_POSTGRES.
public sealed class WalletLedgerPostgresTests : IAsyncLifetime
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
        // Saved straight, as workspaces from before wallets were: neither has a wallet yet.
        db.Workspaces.AddRange(new Workspace { Id = "ws-1", OwnerId = "owner" }, new Workspace { Id = "ws-2", OwnerId = "owner" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private EfPaymentOrderRepository Orders() => new(new Contexts(_options!));

    private sealed class Contexts(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private async Task<PaymentOrder> PendingAsync(string workspace = "ws-1", long total = 800_000)
    {
        PaymentOrder order = new()
        {
            WorkspaceId = workspace,
            AgentId = "agt_test",
            ConnectionId = Guid.NewGuid(),
            CustomerSender = "234801",
            Reference = "prt_" + Guid.NewGuid().ToString("N"),
            IdempotencyKey = Guid.NewGuid().ToString(),
            Status = PaymentOrderStatus.Pending,
            Currency = "NGN",
            TotalMinor = total,
            Lines = [new PaymentOrderLine(Guid.NewGuid(), null, "Quilted bag", null, 1, total, total)],
            Provider = "accelerate",
        };
        Assert.True(await Orders().TryAddAsync(order, default));
        return order;
    }

    private static PaymentOrder PaidCopy(PaymentOrder order)
    {
        PaymentOrder paid = new()
        {
            Id = order.Id,
            WorkspaceId = order.WorkspaceId,
            AgentId = order.AgentId,
            ConnectionId = order.ConnectionId,
            CustomerSender = order.CustomerSender,
            Reference = order.Reference,
            IdempotencyKey = order.IdempotencyKey,
            Currency = order.Currency,
            TotalMinor = order.TotalMinor,
            Lines = order.Lines,
            Provider = order.Provider,
            Status = PaymentOrderStatus.Paid,
            PaidAt = DateTime.UtcNow,
            PaidMinor = order.TotalMinor,
            ProviderTransactionId = "txn-" + order.Reference[^6..],
            UpdatedAt = DateTime.UtcNow,
        };
        return paid;
    }

    private static WalletTransaction Credit(PaymentOrder order, string? workspace = null) => new()
    {
        WorkspaceId = workspace ?? order.WorkspaceId,
        Type = WalletTransactionType.Credit,
        Kind = WalletTransactionKind.Payment,
        ActionBy = WalletTransaction.AccelerateActor,
        AmountMinor = order.TotalMinor,
        Reference = order.Reference,
        PaymentOrderId = order.Id,
        ProviderTransactionId = "txn-" + order.Reference[^6..],
        Description = "Payment from a customer for 1 x Quilted bag",
    };

    private async Task<(long Balance, List<WalletTransaction> Entries)> WalletAsync(string workspace)
    {
        await using ApplicationDbContext db = new(_options!);
        long balance = await db.WorkspaceWallets.Where(w => w.WorkspaceId == workspace).Select(w => w.BalanceMinor).SingleAsync();
        List<WalletTransaction> entries = await db.WalletTransactions.Where(t => t.WorkspaceId == workspace).OrderBy(t => t.BalanceAfterMinor).ToListAsync();
        return (balance, entries);
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task A_payment_marks_the_order_paid_and_credits_the_wallet_in_one_go()
    {
        PaymentOrder order = await PendingAsync();
        WalletTransaction credit = Credit(order);

        Assert.True(await Orders().TryRecordPaymentAsync(PaidCopy(order), PaymentOrderStatus.Pending, credit, default));

        Assert.Equal(800_000, credit.BalanceAfterMinor);
        Assert.Equal(PaymentOrderStatus.Paid, (await Orders().FindAsync("ws-1", order.Id, default))!.Status);
        (long balance, List<WalletTransaction> entries) = await WalletAsync("ws-1");
        Assert.Equal(800_000, balance);
        WalletTransaction saved = Assert.Single(entries);
        Assert.Equal((WalletTransactionType.Credit, WalletTransactionStatus.Success, WalletTransactionKind.Payment, 800_000L, 800_000L),
            (saved.Type, saved.Status, saved.Kind, saved.AmountMinor, saved.BalanceAfterMinor));
        Assert.Equal((order.Reference, (Guid?)order.Id, WalletTransaction.AccelerateActor), (saved.Reference, saved.PaymentOrderId, saved.ActionBy));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task The_same_payment_arriving_several_times_at_once_is_credited_once()
    {
        PaymentOrder order = await PendingAsync();

        bool[] recorded = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Orders().TryRecordPaymentAsync(PaidCopy(order), PaymentOrderStatus.Pending, Credit(order), default)));

        Assert.Single(recorded, r => r);
        (long balance, List<WalletTransaction> entries) = await WalletAsync("ws-1");
        Assert.Equal(800_000, balance);
        Assert.Single(entries);
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task Payments_to_one_wallet_at_once_each_get_the_balance_just_after_them()
    {
        PaymentOrder[] orders = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => PendingAsync(total: i * 100_000)));

        await Task.WhenAll(orders.Select(o => Orders().TryRecordPaymentAsync(PaidCopy(o), PaymentOrderStatus.Pending, Credit(o), default)));

        (long balance, List<WalletTransaction> entries) = await WalletAsync("ws-1");
        Assert.Equal(3_600_000, balance);
        Assert.Equal(8, entries.Count);
        // Each balance-after is the one before it plus its own amount, ending at the wallet's balance.
        long running = 0;
        foreach (WalletTransaction entry in entries)
        {
            running += entry.AmountMinor;
            Assert.Equal(running, entry.BalanceAfterMinor);
        }

        Assert.Equal(balance, running);
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task A_failed_entry_is_on_record_but_moves_nothing()
    {
        PaymentOrder paid = await PendingAsync(total: 300_000);
        PaymentOrder failed = await PendingAsync(total: 500_000);
        Assert.True(await Orders().TryRecordPaymentAsync(PaidCopy(paid), PaymentOrderStatus.Pending, Credit(paid), default));
        WalletTransaction notThrough = Credit(failed);
        notThrough.Status = WalletTransactionStatus.Failed;

        Assert.True(await Orders().TryRecordPaymentAsync(PaidCopy(failed), PaymentOrderStatus.Pending, notThrough, default));

        Assert.Equal(300_000, notThrough.BalanceAfterMinor);
        (long balance, List<WalletTransaction> entries) = await WalletAsync("ws-1");
        Assert.Equal((300_000L, 2), (balance, entries.Count));
        await using ApplicationDbContext read = new(_options!);
        WalletResponse wallet = await new WalletService(new EfWalletRepository(read)).GetAsync("ws-1", default);
        Assert.Equal((3_000m, 3_000m), (wallet.Balance, wallet.TotalIn));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task When_the_credit_cant_be_saved_the_order_stays_unpaid()
    {
        PaymentOrder order = await PendingAsync();

        // A credit for a workspace that doesn't exist can't be saved, and takes the order's update back with it.
        await Assert.ThrowsAnyAsync<NpgsqlException>(() =>
            Orders().TryRecordPaymentAsync(PaidCopy(order), PaymentOrderStatus.Pending, Credit(order, workspace: "no-such-workspace"), default));

        PaymentOrder found = (await Orders().FindAsync("ws-1", order.Id, default))!;
        Assert.Equal((PaymentOrderStatus.Pending, (DateTime?)null), (found.Status, found.PaidAt));
        await using ApplicationDbContext db = new(_options!);
        Assert.Equal(0, await db.WalletTransactions.CountAsync());
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task The_dashboard_reads_its_own_wallet_newest_first_a_page_at_a_time()
    {
        PaymentOrder first = await PendingAsync(total: 100_000);
        PaymentOrder second = await PendingAsync(total: 250_050);
        PaymentOrder elsewhere = await PendingAsync("ws-2", 999_900);
        foreach (PaymentOrder o in new[] { first, second, elsewhere })
        {
            Assert.True(await Orders().TryRecordPaymentAsync(PaidCopy(o), PaymentOrderStatus.Pending, Credit(o), default));
        }

        // A hand-made debit, as a payout will be, so both ways show.
        await using (ApplicationDbContext db = new(_options!))
        {
            db.WalletTransactions.Add(new WalletTransaction
            {
                WorkspaceId = "ws-1", Type = WalletTransactionType.Debit, Kind = WalletTransactionKind.Payout, AmountMinor = 50_000,
                BalanceAfterMinor = 300_050, Reference = "payout-1", Description = "Paid out to the business", ActionBy = "owner",
                CreatedAt = DateTime.UtcNow.AddSeconds(5),
            });
            // A payout that didn't go through: on record, counted nowhere.
            db.WalletTransactions.Add(new WalletTransaction
            {
                WorkspaceId = "ws-1", Type = WalletTransactionType.Debit, Status = WalletTransactionStatus.Failed, Kind = WalletTransactionKind.Payout,
                AmountMinor = 70_000, BalanceAfterMinor = 300_050, Reference = "payout-2", Description = "Payout refused by the bank", ActionBy = "owner",
                CreatedAt = DateTime.UtcNow.AddSeconds(6),
            });
            await db.WorkspaceWallets.Where(w => w.WorkspaceId == "ws-1").ExecuteUpdateAsync(s => s.SetProperty(w => w.BalanceMinor, 300_050));
            await db.SaveChangesAsync();
        }

        await using ApplicationDbContext read = new(_options!);
        WalletService wallets = new(new EfWalletRepository(read));

        WalletResponse wallet = await wallets.GetAsync("ws-1", default);
        Assert.Equal((3_000.50m, "NGN", 3_500.50m, 500m), (wallet.Balance, wallet.Currency, wallet.TotalIn, wallet.TotalOut));

        WalletTransactionsPage page = await wallets.ListAsync("ws-1", new WalletTransactionFilter(), 1, 2, default);
        Assert.Equal((4, 2), (page.Total, page.Items.Count));
        Assert.Equal([WalletTransactionStatus.Failed, WalletTransactionStatus.Success], page.Items.Select(i => i.Status));
        Assert.Equal([700m, 500m], page.Items.Select(i => i.Amount));
        Assert.Equal(("owner", "A team member"), (page.Items[1].ActionBy, page.Items[1].ByName));

        WalletTransactionsPage failed = await wallets.ListAsync("ws-1", new WalletTransactionFilter(Status: WalletTransactionStatus.Failed), 1, 20, default);
        Assert.Equal("payout-2", Assert.Single(failed.Items).Reference);

        WalletTransactionsPage moneyIn = await wallets.ListAsync("ws-1", new WalletTransactionFilter(WalletTransactionType.Credit), 1, 20, default);
        Assert.Equal([second.Reference, first.Reference], moneyIn.Items.Select(i => i.Reference));
        Assert.All(moneyIn.Items, i => Assert.Equal((WalletTransactionKind.Payment, "Accelerate (payment received)"), (i.Kind, i.ByName)));

        Assert.Equal(9_999m, (await wallets.GetAsync("ws-2", default)).Balance);
    }

    [Fact]
    public void Types_statuses_and_kinds_go_out_as_their_names_as_the_dashboard_reads_them()
    {
        WalletTransactionResponse entry = new(Guid.Empty, WalletTransactionType.Debit, WalletTransactionStatus.Failed, WalletTransactionKind.Payout,
            5m, 0m, "NGN", "x", "r", null, null, "owner", "A team member", DateTime.UnixEpoch);

        string json = System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"type\":\"Debit\"", json);
        Assert.Contains("\"status\":\"Failed\"", json);
        Assert.Contains("\"kind\":\"Payout\"", json);
    }
}
