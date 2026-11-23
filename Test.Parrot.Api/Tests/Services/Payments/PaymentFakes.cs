using System.Text.Json;
using Parrot.Application.Agents;
using Parrot.Application.FeatureFlags;
using Parrot.Application.Payments;
using Parrot.Domain.Entities;

namespace Test.Parrot.Api.Tests.Services.Payments;

/// <summary>Orders kept in memory, with the database's rules that matter here: one order per idempotency key, and a status change that only lands if nobody changed the order first.</summary>
public sealed class FakePaymentOrderRepository : IPaymentOrderRepository
{
    public List<PaymentOrder> Orders { get; } = [];

    /// <summary>Runs just before a conditional update, to change the order underneath it.</summary>
    public Action<PaymentOrder>? BeforeConditionalUpdate { get; set; }

    public Task<PaymentOrder?> FindByIdempotencyKeyAsync(string workspaceId, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(Copy(Orders.FirstOrDefault(o => o.WorkspaceId == workspaceId && o.IdempotencyKey == idempotencyKey)));

    public Task<PaymentOrder?> FindAsync(string workspaceId, Guid orderId, CancellationToken ct) =>
        Task.FromResult(Copy(Orders.FirstOrDefault(o => o.WorkspaceId == workspaceId && o.Id == orderId)));

    public Task<PaymentOrder?> FindByReferenceAsync(string reference, CancellationToken ct) =>
        Task.FromResult(Copy(Orders.FirstOrDefault(o => o.Reference == reference)));

    public Task<bool> TryAddAsync(PaymentOrder order, CancellationToken ct)
    {
        if (Orders.Any(o => o.WorkspaceId == order.WorkspaceId && o.IdempotencyKey == order.IdempotencyKey))
            return Task.FromResult(false);
        Orders.Add(Copy(order)!);
        return Task.FromResult(true);
    }

    public Task UpdateAsync(PaymentOrder order, CancellationToken ct)
    {
        Orders[Orders.FindIndex(o => o.Id == order.Id)] = Copy(order)!;
        return Task.CompletedTask;
    }

    public Task<bool> TryUpdateFromAsync(PaymentOrder order, PaymentOrderStatus expected, CancellationToken ct)
    {
        PaymentOrder stored = Orders.Single(o => o.Id == order.Id);
        BeforeConditionalUpdate?.Invoke(stored);
        BeforeConditionalUpdate = null;
        stored = Orders.Single(o => o.Id == order.Id);
        if (stored.Status != expected || stored.PaidAt is not null) return Task.FromResult(false);
        Orders[Orders.IndexOf(stored)] = Copy(order)!;
        return Task.FromResult(true);
    }

    /// <summary>Wallet credits recorded with payments, in order, each with its balance after.</summary>
    public List<WalletTransaction> Credits { get; } = [];

    public async Task<bool> TryRecordPaymentAsync(PaymentOrder order, PaymentOrderStatus expected, WalletTransaction credit, CancellationToken ct)
    {
        if (!await TryUpdateFromAsync(order, expected, ct)) return false;
        if (Credits.Any(c => c.Reference == credit.Reference)) return true;
        credit.BalanceAfterMinor = Credits.Where(c => c.WorkspaceId == credit.WorkspaceId).Sum(c => c.AmountMinor) + credit.AmountMinor;
        Credits.Add(credit);
        return true;
    }

    public Task<bool> TryMoveAsync(PaymentOrder order, PaymentOrderStatus expected, CancellationToken ct)
    {
        PaymentOrder stored = Orders.Single(o => o.Id == order.Id && o.WorkspaceId == order.WorkspaceId);
        BeforeConditionalUpdate?.Invoke(stored);
        BeforeConditionalUpdate = null;
        stored = Orders.Single(o => o.Id == order.Id);
        if (stored.Status != expected || stored.PaidAt is not null) return Task.FromResult(false);
        Orders[Orders.IndexOf(stored)] = Copy(order)!;
        return Task.FromResult(true);
    }

    public Task<PaymentOrder?> FindOpenCartAsync(string workspaceId, Guid connectionId, string sender, DateTime now, CancellationToken ct) =>
        Task.FromResult(Copy(Orders.Where(o => o.WorkspaceId == workspaceId && o.ConnectionId == connectionId && o.CustomerSender == sender
            && o.Status == PaymentOrderStatus.AwaitingCheckout && o.PaidAt is null && o.ExpiresAt > now).Reverse().OrderByDescending(o => o.CreatedAt).FirstOrDefault()));

    public Task<PaymentOrder?> FindLatestCartAsync(string workspaceId, Guid connectionId, string sender, CancellationToken ct) =>
        Task.FromResult(Copy(Orders.Where(o => o.WorkspaceId == workspaceId && o.ConnectionId == connectionId && o.CustomerSender == sender)
            .Reverse().OrderByDescending(o => o.CreatedAt).FirstOrDefault()));

    public Task<bool> TryReplaceCartAsync(PaymentOrder order, DateTime readAt, CancellationToken ct)
    {
        PaymentOrder stored = Orders.Single(o => o.Id == order.Id && o.WorkspaceId == order.WorkspaceId);
        BeforeConditionalUpdate?.Invoke(stored);
        BeforeConditionalUpdate = null;
        stored = Orders.Single(o => o.Id == order.Id);
        if (stored.Status != PaymentOrderStatus.AwaitingCheckout || stored.PaidAt is not null || stored.UpdatedAt != readAt) return Task.FromResult(false);
        Orders[Orders.IndexOf(stored)] = Copy(order)!;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PaymentOrder>> CancelOpenAsync(string workspaceId, Guid connectionId, string sender, Guid keep, DateTime at, CancellationToken ct)
    {
        List<PaymentOrder> open = Orders.Where(o => o.WorkspaceId == workspaceId && o.ConnectionId == connectionId && o.CustomerSender == sender
            && o.Id != keep && o.Status is PaymentOrderStatus.Pending or PaymentOrderStatus.AwaitingCheckout && o.PaidAt is null).ToList();
        open.ForEach(o => o.Status = PaymentOrderStatus.Cancelled);
        return Task.FromResult<IReadOnlyList<PaymentOrder>>(open.Select(o => Copy(o)!).ToList());
    }

    public Task<IReadOnlyList<PaymentOrder>> ListDueAsync(DateTime now, DateTime stuckBefore, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PaymentOrder>>(Orders
            .Where(o => o.PaidAt is null && ((o.Status is PaymentOrderStatus.Pending or PaymentOrderStatus.AwaitingCheckout && o.ExpiresAt <= now)
                || (o.Status == PaymentOrderStatus.Creating && o.UpdatedAt <= stuckBefore)))
            .Take(limit).Select(o => Copy(o)!).ToList());

    public PaymentOrder Get(Guid id) => Orders.Single(o => o.Id == id);

    // Callers get their own copy, as from a database, so a change only counts once it's saved.
    private static PaymentOrder? Copy(PaymentOrder? order) => order is null ? null : new PaymentOrder
    {
        Id = order.Id, WorkspaceId = order.WorkspaceId, AgentId = order.AgentId, ConnectionId = order.ConnectionId, CustomerSender = order.CustomerSender,
        CustomerName = order.CustomerName, CustomerEmail = order.CustomerEmail, CustomerPhone = order.CustomerPhone, Note = order.Note,
        Reference = order.Reference, IdempotencyKey = order.IdempotencyKey, Status = order.Status, Currency = order.Currency, TotalMinor = order.TotalMinor,
        Lines = [.. order.Lines], Provider = order.Provider, ProviderSessionId = order.ProviderSessionId, PaymentUrl = order.PaymentUrl,
        FailureReason = order.FailureReason, CreatedAt = order.CreatedAt, UpdatedAt = order.UpdatedAt, ExpiresAt = order.ExpiresAt, PaidAt = order.PaidAt,
        PaidMinor = order.PaidMinor, ProviderTransactionId = order.ProviderTransactionId, AttentionReason = order.AttentionReason,
        QuotedTotalMinor = order.QuotedTotalMinor, HoldMode = order.HoldMode, CheckoutExpiresAt = order.CheckoutExpiresAt, PaymentStartedAt = order.PaymentStartedAt,
    };
}

/// <summary>The catalog's stock holds, kept in memory: what was held, committed and released, and failures on demand.</summary>
public sealed class FakeStockClient : ICatalogStockClient
{
    public List<(string Workspace, string Reference, DateTime ExpiresAt, IReadOnlyList<StockLine> Lines)> Held { get; } = [];

    public List<string> Committed { get; } = [];

    public List<string> Released { get; } = [];

    public string? ShortWith { get; set; }

    /// <summary>Short for some holds only: the message for the lines asked for, or null to hold them.</summary>
    public Func<IReadOnlyList<StockLine>, string?>? ShortIf { get; set; }

    public bool Down { get; set; }

    public IReadOnlyList<StockLine> CommitShort { get; set; } = [];

    public Task HoldAsync(string workspaceId, string reference, DateTime expiresAt, IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        if (Down) throw new CatalogStockUnavailableException("The catalog can't be reached right now.");
        if (ShortWith is not null) throw new StockShortException(ShortWith);
        if (ShortIf?.Invoke(lines) is { } shortMessage) throw new StockShortException(shortMessage);
        Held.Add((workspaceId, reference, expiresAt, lines));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StockLine>> CommitAsync(string workspaceId, string reference, CancellationToken ct)
    {
        if (Down) throw new CatalogStockUnavailableException("The catalog can't be reached right now.");
        Committed.Add(reference);
        return Task.FromResult(CommitShort);
    }

    public Task ReleaseAsync(string workspaceId, string reference, CancellationToken ct)
    {
        if (Down) throw new CatalogStockUnavailableException("The catalog can't be reached right now.");
        Released.Add(reference);
        return Task.CompletedTask;
    }
}

public sealed class FakeNotifier : IPaymentNotifier
{
    public List<PaymentOrder> Told { get; } = [];

    public Task PaidAsync(PaymentOrder order, CancellationToken ct)
    {
        Told.Add(order);
        return Task.CompletedTask;
    }
}

/// <summary>A payment provider that records what it was asked and can be told to fail.</summary>
public sealed class FakePaymentProvider : IPaymentProvider
{
    public List<(string Reference, long AmountMinor, string Description)> Calls { get; } = [];

    /// <summary>Where each link was told to send the customer after paying.</summary>
    public List<string?> ReturnUrls { get; } = [];

    public bool Configured { get; set; } = true;

    public PaymentProviderException? FailWith { get; set; }

    public string Name => "fake";

    public bool IsConfigured => Configured;

    public Task<PaymentLink> CreateLinkAsync(string reference, long amountMinor, string description, string? returnUrl, CancellationToken ct)
    {
        Calls.Add((reference, amountMinor, description));
        ReturnUrls.Add(returnUrl);
        if (FailWith is not null) throw FailWith;
        return Task.FromResult(new PaymentLink("session-" + reference, "https://pay.example/" + reference));
    }
}

public sealed class FakeFlags(bool on = true) : IFeatureFlagService
{
    public bool On { get; set; } = on;

    public Task<bool> IsEnabledAsync(string key, bool defaultValue, CancellationToken cancellationToken = default) =>
        Task.FromResult(key == FeatureFlagKeys.PaymentLinks ? On : defaultValue);
}

/// <summary>
/// The catalog's get_prices, answered from products set up in the test, in the catalog's own
/// shape: names marked as tenant text, products it doesn't sell listed in not_on_sale.
/// </summary>
public sealed class FakePriceCatalog : ICatalogToolsClient
{
    private readonly Dictionary<Guid, Func<object>> _products = [];
    private readonly Dictionary<Guid, (string Name, decimal? Price, int? Left, bool Available, string Currency)> _simple = [];

    public CatalogToolException? FailWith { get; set; }

    public List<(string Workspace, string Tool)> Calls { get; } = [];

    public Guid Product(string name, decimal? price, int? left = null, bool available = true, string currency = "NGN")
    {
        Guid id = Guid.NewGuid();
        _simple[id] = (name, price, left, available, currency);
        _products[id] = () =>
        {
            (string n, decimal? p, int? l, bool a, string c) = _simple[id];
            return new { product_id = id, name = Text(n), available = a, price_on_request = p is null, price = p, currency = c, quantity_left = a ? l : 0, variants = Array.Empty<object>() };
        };
        return id;
    }

    /// <summary>The business changes a product: a new price, how many are left, or takes it off sale.</summary>
    public void Change(Guid id, decimal? price = null, int? left = null, bool? available = null)
    {
        var p = _simple[id];
        _simple[id] = (p.Name, price ?? p.Price, left ?? p.Left, available ?? p.Available, p.Currency);
    }

    public (Guid Product, Guid[] Options) WithOptions(string name, params (string Name, decimal Price, bool Available, int? Left)[] options)
    {
        Guid id = Guid.NewGuid();
        Guid[] optionIds = options.Select(_ => Guid.NewGuid()).ToArray();
        _products[id] = () => new
        {
            product_id = id, name = Text(name), available = options.Any(o => o.Available), price_on_request = false,
            price = options.Where(o => o.Available).Select(o => (decimal?)o.Price).Min(), currency = "NGN", quantity_left = (int?)null,
            variants = options.Select((o, i) => new { variant_id = optionIds[i], name = Text(o.Name), price = o.Price, available = o.Available, quantity_left = o.Available ? o.Left : 0 }).ToArray(),
        };
        return (id, optionIds);
    }

    public Task<JsonElement> CallAsync(string workspaceId, string tool, object arguments, CancellationToken ct)
    {
        Calls.Add((workspaceId, tool));
        if (FailWith is not null) throw FailWith;
        Guid[] ids = JsonSerializer.SerializeToElement(arguments).GetProperty("Ids").Deserialize<Guid[]>()!;
        object answer = new
        {
            tool,
            result = new
            {
                items = ids.Where(_products.ContainsKey).Select(id => _products[id]()).ToArray(),
                closest = Array.Empty<object>(),
                not_on_sale = ids.Where(id => !_products.ContainsKey(id)).ToArray(),
            },
        };
        return Task.FromResult(JsonSerializer.SerializeToElement(answer));
    }

    private static object Text(string value) => new { value, kind = "untrusted_text" };
}

/// <summary>Workspaces' checkout settings, in memory.</summary>
public sealed class FakeWorkspacePaymentSettingsRepository : IWorkspacePaymentSettingsRepository
{
    public Dictionary<string, WorkspacePaymentSettings> Rows { get; } = [];

    public Task<WorkspacePaymentSettings?> GetAsync(string workspaceId, CancellationToken ct) =>
        Task.FromResult(Rows.TryGetValue(workspaceId, out WorkspacePaymentSettings? row) ? row : null);

    public Task SaveAsync(WorkspacePaymentSettings settings, CancellationToken ct)
    {
        Rows[settings.WorkspaceId] = settings;
        return Task.CompletedTask;
    }

    public void Set(string workspaceId, CheckoutHoldMode hold = CheckoutHoldMode.AtPay, PriceChangeRule rule = PriceChangeRule.ConfirmNewPrice, int checkoutMinutes = 30, int paymentMinutes = 15) =>
        Rows[workspaceId] = new WorkspacePaymentSettings { WorkspaceId = workspaceId, HoldMode = hold, PriceChangeRule = rule, CheckoutMinutes = checkoutMinutes, PaymentMinutes = paymentMinutes };
}
