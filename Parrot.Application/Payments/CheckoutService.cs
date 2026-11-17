using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.Agents;
using Parrot.Application.FeatureFlags;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;

namespace Parrot.Application.Payments;

/// <summary>
/// Checkouts: the agent makes one for what a customer agreed to buy and sends its link; the
/// customer opens it, sees the live prices (old and new, when one changed) and presses Pay,
/// which is when the payment link is made. The agent names products and quantities only:
/// each line is priced from the live catalog with the same rules as get_prices, when the
/// checkout is made and again when Pay is pressed. Every order belongs to one workspace, and
/// every lookup is by workspace. How long things stay open, when stock is kept aside and what
/// a price change does are the workspace's own settings.
/// </summary>
public sealed class CheckoutService(
    IPaymentOrderRepository orders,
    IPaymentProvider provider,
    ICatalogToolsClient catalog,
    ICatalogStockClient stock,
    IAgentRepository agents,
    IWorkspaceConnectionDirectory connections,
    IWorkspaceRepository workspaces,
    IFeatureFlagService flags,
    WorkspacePaymentSettingsService workspaceSettings,
    CheckoutLinks links,
    IOptions<PaymentSettings> settings,
    TimeProvider clock,
    ILogger<CheckoutService> logger)
{
    public const int MaxItems = 20;
    public const int MaxQuantity = 999;
    public const string CheckoutReady = "checkout_ready";

    /// <summary>The only currency the payment account takes.</summary>
    public const string Currency = "NGN";

    private const int MaxKey = 100;
    private const int MaxSender = 256;
    private const int MaxDescription = 500;

    private static readonly JsonSerializerOptions CatalogJson = new () { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    /// <summary>Naira to kobo, to the nearest kobo.</summary>
    public static long ToMinor(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    public static decimal FromMinor(long minor) => minor / 100m;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---- The agent: making a checkout ---------------------------------------------------

    public async Task<CheckoutResult> CreateAsync(string workspaceId, CreateCheckoutRequest request, CancellationToken ct)
    {
        await EnsureOnAsync(ct);
        string key = Required(request.IdempotencyKey, MaxKey, "idempotency_key");
        string sender = Required(request.Sender, MaxSender, "sender");

        PaymentOrder? earlier = await orders.FindByIdempotencyKeyAsync(workspaceId, key, ct);
        if (earlier is not null)
        {
            return Again(earlier);
        }

        List<CheckoutItem> items = Merge(request.Items);
        await CheckConversationAsync(workspaceId, request.AgentId, request.ConnectionId);

        List<PaymentOrderLine> lines = Agreed(await QuoteAsync(workspaceId, items, countStock: true, ct));
        long total = lines.Sum(l => l.LineTotalMinor);
        CheckLimit(total);

        WorkspacePaymentSettings rules = await workspaceSettings.ResolveAsync(workspaceId, ct);
        DateTime checkoutExpires = Now.AddMinutes(rules.CheckoutMinutes);
        PaymentOrder order = new ()
        {
            WorkspaceId = workspaceId,
            AgentId = request.AgentId,
            ConnectionId = request.ConnectionId,
            CustomerSender = sender,
            CustomerName = Optional(request.CustomerName, 120),
            CustomerEmail = Optional(request.CustomerEmail, 254),
            CustomerPhone = Optional(request.CustomerPhone, 32),
            Note = Optional(request.Note, 500),
            Reference = "prt_" + Guid.NewGuid().ToString("N"),
            IdempotencyKey = key,
            Status = PaymentOrderStatus.AwaitingCheckout,
            Currency = Currency,
            TotalMinor = total,
            QuotedTotalMinor = total,
            Lines = lines,
            HoldMode = rules.HoldMode,
            Provider = provider.Name,
            CreatedAt = Now,
            UpdatedAt = Now,
            CheckoutExpiresAt = checkoutExpires,
            ExpiresAt = checkoutExpires,
        };

        if (!await orders.TryAddAsync(order, ct))
        {
            // The same request, sent twice at once: answer with the one that was saved.
            PaymentOrder saved = await orders.FindByIdempotencyKeyAsync(workspaceId, key, ct)
                ?? throw new InvalidOperationException("An order with this idempotency key was reported but not found.");
            return Again(saved);
        }

        if (rules.HoldMode == CheckoutHoldMode.AtCheckout)
        {
            // Kept from now until the latest a payment started at the last minute could run; the sweep
            // lets it go as soon as the checkout or its payment lapses, so it never outlives the order.
            DateTime heldUntil = checkoutExpires.AddMinutes(WorkspacePaymentSettingsService.MaxPaymentMinutes);
            try
            {
                await stock.HoldAsync(workspaceId, order.Reference, heldUntil, StockLines(lines), ct);
            }
            catch (StockShortException ex)
            {
                await FailAsync(order, $"Stock taken: {ex.Message}");
                throw new CheckoutRefusedException($"{ex.Message} Someone may have just bought it. Check get_prices and agree the order again with the customer.");
            }
            catch (CatalogStockUnavailableException ex)
            {
                await FailAsync(order, ex.Message);
                throw new CheckoutUnavailableException("Stock can't be checked right now. Tell the customer you'll send the checkout link shortly.");
            }
        }

        // One open checkout per conversation: the customer pays the latest, and the older ones' stock goes back.
        IReadOnlyList<PaymentOrder> replaced = await orders.CancelOpenAsync(workspaceId, order.ConnectionId, order.CustomerSender, order.Id, Now, CancellationToken.None);
        foreach (PaymentOrder older in replaced)
        {
            await ReleaseQuietlyAsync(older);
        }

        logger.LogInformation("Checkout made. Workspace={Workspace} Order={Order} Agent={Agent} TotalMinor={Total} Hold={Hold} Replaced={Replaced}",
            workspaceId, order.Id, order.AgentId, total, order.HoldMode, replaced.Count);
        return Result(order);
    }

    // ---- The customer: the checkout page ------------------------------------------------

    /// <summary>The checkout a link names, priced live while it's open.</summary>
    public async Task<CheckoutView> GetAsync(string token, CancellationToken ct)
    {
        PaymentOrder order = await FindAsync(token, ct);
        return await ViewAsync(order, ct);
    }

    /// <summary>
    /// The customer pressed Pay: price it again, keep the stock aside if the business asks, and
    /// make the payment link. Refused, with the checkout as it is now, if anything changed since
    /// the customer saw it. Pressing Pay again while the link is open returns the same link.
    /// </summary>
    public async Task<PayCheckoutResult> PayAsync(string token, PayCheckoutRequest request, CancellationToken ct)
    {
        PaymentOrder order = await FindAsync(token, ct);
        if (order.Status == PaymentOrderStatus.Pending && order.ExpiresAt > Now && order.PaymentUrl is not null)
        {
            return new PayCheckoutResult(order.PaymentUrl, order.ExpiresAt);
        }

        if (order.Status != PaymentOrderStatus.AwaitingCheckout || order.ExpiresAt <= Now)
        {
            CheckoutView closed = await ViewAsync(order, ct);
            throw new CheckoutChangedException(closed.Message ?? "This checkout can't be paid any more.", closed);
        }

        await EnsureOnAsync(ct, paying: true);
        WorkspacePaymentSettings rules = await workspaceSettings.ResolveAsync(order.WorkspaceId, ct);
        List<LineQuote> quotes;
        try
        {
            // Stock kept aside since the checkout was made is this order's own: don't count it against it.
            quotes = await QuoteAsync(order.WorkspaceId, Items(order), countStock: order.HoldMode != CheckoutHoldMode.AtCheckout, ct);
        }
        catch (CheckoutRefusedException ex)
        {
            throw new CheckoutChangedException(ex.Message, Open(order, Business(await workspaces.GetByIdAsync(order.WorkspaceId)), null, rules, ex.Message));
        }

        Workspace? workspace = await workspaces.GetByIdAsync(order.WorkspaceId);
        CheckoutView now = Open(order, Business(workspace), quotes, rules, null);
        if (!now.CanPay)
        {
            throw new CheckoutChangedException(now.Message ?? "This checkout can't be paid as it stands.", now);
        }

        if (ToMinor(now.Total) != ToMinor(request.ExpectedTotal))
        {
            throw new CheckoutChangedException($"The total is now {Currency} {now.Total:N2}. Check it and press Pay again.", now);
        }

        // What's paid is today's price; the agent's quote stays on each line, so the order shows what changed.
        List<PaymentOrderLine> shown = order.Lines;
        long shownTotal = order.TotalMinor;
        List<PaymentOrderLine> lines = Agreed(quotes)
            .Select(l => l with { QuotedUnitPriceMinor = shown.FirstOrDefault(o => o.ProductId == l.ProductId && o.VariantId == l.VariantId)?.QuotedUnitMinor ?? l.UnitPriceMinor })
            .ToList();
        long total = lines.Sum(l => l.LineTotalMinor);
        CheckLimit(total);

        // Only one press of Pay starts the payment; the other is told it's on its way.
        PaymentOrder starting = order;
        starting.Status = PaymentOrderStatus.Creating;
        starting.Lines = lines;
        starting.TotalMinor = total;
        starting.UpdatedAt = Now;
        if (!await orders.TryMoveAsync(starting, PaymentOrderStatus.AwaitingCheckout, ct))
        {
            PaymentOrder? current = await orders.FindAsync(order.WorkspaceId, order.Id, ct);
            if (current is { Status: PaymentOrderStatus.Pending, PaymentUrl: not null })
            {
                return new PayCheckoutResult(current.PaymentUrl, current.ExpiresAt);
            }

            throw new CheckoutUnavailableException("Your payment is being prepared. Wait a moment, then press Pay again.");
        }

        DateTime linkExpires = Now.AddMinutes(rules.PaymentMinutes);
        if (starting.HoldMode == CheckoutHoldMode.AtPay)
        {
            try
            {
                await stock.HoldAsync(starting.WorkspaceId, starting.Reference, linkExpires, StockLines(lines), ct);
            }
            catch (StockShortException ex)
            {
                await BackToCheckoutAsync(starting, shown, shownTotal);
                CheckoutView changed = await ViewAsync(starting, ct);
                throw new CheckoutChangedException($"{ex.Message} Someone may have just bought it.", changed);
            }
            catch (CatalogStockUnavailableException ex)
            {
                await BackToCheckoutAsync(starting, shown, shownTotal);
                logger.LogWarning("Pay pressed but stock couldn't be checked. Order={Order} Reason={Reason}", order.Id, ex.Message);
                throw new CheckoutUnavailableException("We can't check stock right now. Try again in a few minutes.");
            }
        }

        string description = await DescribeAsync(workspace, lines);
        PaymentLink link;
        try
        {
            link = await provider.CreateLinkAsync(starting.Reference, total, description, ct);
        }
        catch (PaymentProviderException ex)
        {
            // One payment link per order (the provider is given the order's reference once), so a
            // failure here ends the checkout: the business sends a new one.
            await FailAsync(starting, ex.Code is null ? ex.Message : $"{ex.Code}: {ex.Message}");
            await ReleaseQuietlyAsync(starting);
            throw new CheckoutUnavailableException("Payment couldn't be started just now. Message the business for a new checkout link.");
        }

        starting.Status = PaymentOrderStatus.Pending;
        starting.ProviderSessionId = link.SessionId;
        starting.PaymentUrl = link.Url;
        starting.PaymentStartedAt = Now;
        starting.ExpiresAt = linkExpires;
        starting.UpdatedAt = Now;
        await orders.UpdateAsync(starting, CancellationToken.None);

        logger.LogInformation("Checkout paying. Workspace={Workspace} Order={Order} TotalMinor={Total} QuotedMinor={Quoted} Hold={Hold}",
            starting.WorkspaceId, starting.Id, total, starting.QuotedTotalMinor, starting.HoldMode);
        return new PayCheckoutResult(link.Url, linkExpires);
    }

    /// <summary>The order a checkout link names, in the workspace the link names.</summary>
    private async Task<PaymentOrder> FindAsync(string token, CancellationToken ct)
    {
        (Guid orderId, string workspaceId) = links.Read(token) ?? throw new CheckoutNotFoundException();
        return await orders.FindAsync(workspaceId, orderId, ct) ?? throw new CheckoutNotFoundException();
    }

    private async Task<CheckoutView> ViewAsync(PaymentOrder order, CancellationToken ct)
    {
        Workspace? workspace = await workspaces.GetByIdAsync(order.WorkspaceId);
        (string name, string? logo) = Business(workspace);
        bool lapsed = order.ExpiresAt <= Now;
        string state = order.Status switch
        {
            PaymentOrderStatus.AwaitingCheckout when !lapsed => CheckoutState.Open,
            PaymentOrderStatus.Creating => CheckoutState.Paying,
            PaymentOrderStatus.Pending when !lapsed => CheckoutState.Paying,
            PaymentOrderStatus.Paid or PaymentOrderStatus.PaidOutOfStock or PaymentOrderStatus.NeedsAttention => CheckoutState.Paid,
            PaymentOrderStatus.Cancelled => CheckoutState.Replaced,
            PaymentOrderStatus.Failed => CheckoutState.Unavailable,
            _ => CheckoutState.Expired,
        };

        if (state == CheckoutState.Open)
        {
            WorkspacePaymentSettings rules = await workspaceSettings.ResolveAsync(order.WorkspaceId, ct);
            try
            {
                List<LineQuote> quotes = await QuoteAsync(order.WorkspaceId, Items(order), countStock: order.HoldMode != CheckoutHoldMode.AtCheckout, ct);
                return Open(order, (name, logo), quotes, rules, null);
            }
            catch (CheckoutRefusedException ex)
            {
                return Open(order, (name, logo), null, rules, ex.Message);
            }
            catch (CheckoutUnavailableException)
            {
                return Open(order, (name, logo), null, rules, "We can't check prices right now. Try again in a few minutes.");
            }
        }

        string message = state switch
        {
            CheckoutState.Paying => order.PaymentUrl is null ? "Your payment is being prepared." : "Finish paying on the payment page.",
            CheckoutState.Paid => "Paid. Thank you! The business will be in touch.",
            CheckoutState.Replaced => "This checkout was replaced by a newer one. Use the latest link the business sent you.",
            CheckoutState.Unavailable => "This checkout can't be paid. Message the business for a new link.",
            _ => "This checkout has expired. Message the business for a new link.",
        };

        CheckoutLineView[] stored = order.Lines.Select(l => new CheckoutLineView(l.Name, l.VariantName, l.Quantity,
            FromMinor(l.QuotedUnitMinor), FromMinor(l.UnitPriceMinor), FromMinor(l.LineTotalMinor), l.UnitPriceMinor != l.QuotedUnitMinor, null)).ToArray();
        WorkspacePaymentSettings shownRules = await workspaceSettings.ResolveAsync(order.WorkspaceId, ct);
        return new CheckoutView(state, name, logo, order.CustomerName, order.Currency, stored,
            FromMinor(order.QuotedTotalMinor == 0 ? order.TotalMinor : order.QuotedTotalMinor), FromMinor(order.TotalMinor),
            order.QuotedTotalMinor != 0 && order.TotalMinor != order.QuotedTotalMinor, shownRules.PriceChangeRule, false, message,
            order.ExpiresAt, state == CheckoutState.Paying ? order.PaymentUrl : null);
    }

    /// <summary>An open checkout as the customer sees it: each line at today's price, with what changed.</summary>
    private CheckoutView Open(PaymentOrder order, (string Name, string? Logo) business, List<LineQuote>? quotes, WorkspacePaymentSettings rules, string? problem)
    {
        List<CheckoutLineView> lines = [];
        bool changed = false;
        bool blocked = quotes is null;
        long total = 0;
        for (int i = 0; i < order.Lines.Count; i++)
        {
            PaymentOrderLine line = order.Lines[i];
            LineQuote? quote = quotes?.FirstOrDefault(q => q.Item.ProductId == line.ProductId && q.Item.VariantId == line.VariantId);
            long? unit = quote?.UnitMinor;
            bool lineChanged = unit is not null && unit != line.QuotedUnitMinor;
            changed |= lineChanged;
            blocked |= quote?.Problem is not null;
            total += (unit ?? line.QuotedUnitMinor) * line.Quantity;
            lines.Add(new CheckoutLineView(quote?.Name ?? line.Name, quote is null ? line.VariantName : quote.OptionName, line.Quantity,
                FromMinor(line.QuotedUnitMinor), unit is null ? null : FromMinor(unit.Value), unit is null ? null : FromMinor(unit.Value * line.Quantity),
                lineChanged, quote?.Problem));
        }

        string? message = problem
            ?? (blocked ? "Some items can't be bought as they are. Message the business to sort it out."
            : changed && rules.PriceChangeRule == PriceChangeRule.Block ? "Prices have changed since this checkout was made. Message the business for a new one."
            : changed ? "Some prices have changed since the business sent this. Check the new total before you pay."
            : null);
        bool canPay = !blocked && !(changed && rules.PriceChangeRule == PriceChangeRule.Block);
        return new CheckoutView(CheckoutState.Open, business.Name, business.Logo, order.CustomerName, order.Currency, lines,
            FromMinor(order.QuotedTotalMinor), FromMinor(total), changed, rules.PriceChangeRule, canPay, message, order.ExpiresAt, null);
    }

    private static (string Name, string? Logo) Business(Workspace? workspace)
        => (string.IsNullOrWhiteSpace(workspace?.Name) ? "Your order" : Plain(workspace!.Name!.Trim()), workspace?.LogoUrl);

    /// <summary>Pay couldn't go ahead: the checkout is open again, as the customer last saw it.</summary>
    private async Task BackToCheckoutAsync(PaymentOrder order, List<PaymentOrderLine> lines, long total)
    {
        order.Status = PaymentOrderStatus.AwaitingCheckout;
        order.Lines = lines;
        order.TotalMinor = total;
        order.UpdatedAt = Now;
        await orders.TryMoveAsync(order, PaymentOrderStatus.Creating, CancellationToken.None);
    }

    private static List<CheckoutItem> Items(PaymentOrder order) => order.Lines.Select(l => new CheckoutItem(l.ProductId, l.VariantId, l.Quantity)).ToList();

    private static StockLine[] StockLines(IEnumerable<PaymentOrderLine> lines) => lines.Select(l => new StockLine(l.ProductId, l.VariantId, l.Quantity)).ToArray();

    // ---- Shared ------------------------------------------------------------------------

    /// <summary>
    /// Checkouts are switched on and the checkout page has an address. Making a checkout needs
    /// nothing more: it never talks to the payment provider. Pressing Pay also needs the provider
    /// (paying: true), since that's when the payment link is made.
    /// </summary>
    private async Task EnsureOnAsync(CancellationToken ct, bool paying = false)
    {
        if (!await flags.IsEnabledAsync(FeatureFlagKeys.PaymentLinks, defaultValue: false, ct))
        {
            throw new CheckoutUnavailableException(paying
                ? "Payments aren't available right now. Message the business to pay another way."
                : "Checkouts aren't switched on for this business yet. Tell the customer the business will send payment details.");
        }

        if (!links.IsConfigured)
        {
            logger.LogError("Checkouts are on but the checkout page address (Payments:CheckoutBaseUrl or EmailSettings:BaseUrl) isn't configured.");
            throw new CheckoutUnavailableException("Checkouts aren't set up yet. Tell the customer the business will send payment details.");
        }

        if (paying && (!provider.IsConfigured || string.IsNullOrWhiteSpace(settings.Value.CallbackUrl)))
        {
            logger.LogError("Pay was pressed but the payment provider or its callback URL isn't configured.");
            throw new CheckoutUnavailableException("Payment can't be taken right now. Try again later, or message the business to pay another way.");
        }
    }

    private void CheckLimit(long total)
    {
        if (total > ToMinor(settings.Value.MaxLinkAmount))
        {
            throw new CheckoutRefusedException($"That comes to {FromMinor(total):N2} {Currency}, over the {settings.Value.MaxLinkAmount:N2} {Currency} a checkout may ask for. Tell the customer the business will be in touch to arrange it.");
        }
    }

    private async Task FailAsync(PaymentOrder order, string reason)
    {
        order.Status = PaymentOrderStatus.Failed;
        order.FailureReason = reason.Length > 500 ? reason[..500] : reason;
        order.UpdatedAt = Now;
        await orders.UpdateAsync(order, CancellationToken.None);
        logger.LogWarning("Checkout failed. Workspace={Workspace} Order={Order} Reason={Reason}", order.WorkspaceId, order.Id, order.FailureReason);
    }

    /// <summary>Lets an order's stock go. If the catalog is down, the hold still runs out by itself at its time.</summary>
    private async Task ReleaseQuietlyAsync(PaymentOrder order)
    {
        if (order.HoldMode == CheckoutHoldMode.Never)
        {
            return;
        }

        try
        {
            await stock.ReleaseAsync(order.WorkspaceId, order.Reference, CancellationToken.None);
        }
        catch (CatalogStockUnavailableException ex)
        {
            logger.LogWarning("Stock hold not released; it will run out at its time. Order={Order} Reason={Reason}", order.Id, ex.Message);
        }
    }

    /// <summary>The answer to a request already seen: the same checkout, or why there isn't one.</summary>
    private CheckoutResult Again(PaymentOrder order) => order.Status switch
    {
        PaymentOrderStatus.AwaitingCheckout or PaymentOrderStatus.Pending or PaymentOrderStatus.Creating => Result(order),
        PaymentOrderStatus.Failed => throw new CheckoutUnavailableException("That checkout couldn't be made. Try again with a new idempotency_key."),
        PaymentOrderStatus.Expired => throw new CheckoutRefusedException("That checkout expired. If the customer still wants the items, check get_prices and make a new one with a new idempotency_key."),
        PaymentOrderStatus.Cancelled => throw new CheckoutRefusedException("That checkout was replaced by a newer one for this conversation. Send the newer link."),
        _ => throw new CheckoutRefusedException("That checkout has been paid. Use get_payment_status for where it is."),
    };

    private CheckoutResult Result(PaymentOrder order) => new (
        order.Id,
        CheckoutReady,
        links.For(order),
        FromMinor(order.TotalMinor),
        order.Currency,
        order.Lines.Select(l => new CheckoutLine(l.ProductId, l.VariantId, l.Name, l.VariantName, l.Quantity, FromMinor(l.UnitPriceMinor), FromMinor(l.LineTotalMinor))).ToArray(),
        order.CheckoutExpiresAt,
        "Send the customer checkout_url with the total and what it's for. On that page they see the latest prices and press Pay, which takes them to the payment page. "
        + "It replaces any earlier checkout in this conversation. If a price changes before they pay, the page shows the old and the new price. "
        + "Don't tell the customer the order is paid: use get_payment_status.");

    /// <summary>The same product and option asked for twice is one line.</summary>
    private static List<CheckoutItem> Merge(IReadOnlyList<CheckoutItem>? items)
    {
        if (items is not { Count: > 0 })
        {
            throw new CheckoutRefusedException("Pass at least one item: each with product_id, variant_id when the product has options, and quantity.");
        }

        List<CheckoutItem> merged = [];
        foreach (IGrouping<(Guid ProductId, Guid? VariantId), CheckoutItem> same in items.GroupBy(i => (i.ProductId, i.VariantId)))
        {
            if (same.Key.ProductId == Guid.Empty || same.Key.VariantId == Guid.Empty)
            {
                throw new CheckoutRefusedException("Each item needs a real product_id (and variant_id, when given).");
            }

            if (same.Any(i => i.Quantity < 1))
            {
                throw new CheckoutRefusedException("Each quantity must be at least 1.");
            }

            int quantity = same.Sum(i => i.Quantity);
            if (quantity > MaxQuantity)
            {
                throw new CheckoutRefusedException($"A checkout can have at most {MaxQuantity} of one item.");
            }

            merged.Add(new CheckoutItem(same.Key.ProductId, same.Key.VariantId, quantity));
        }

        if (merged.Count > MaxItems)
        {
            throw new CheckoutRefusedException($"A checkout can have at most {MaxItems} different items.");
        }

        return merged;
    }

    private async Task CheckConversationAsync(string workspaceId, Guid agentId, Guid connectionId)
    {
        Agent? agent = await agents.GetByIdAsync(agentId);
        if (agent is null || agent.WorkspaceId != workspaceId)
        {
            throw new CheckoutRefusedException("agent_id isn't one of this business's agents.");
        }

        IReadOnlyList<(Guid Id, Domain.Enums.MessagingPlatform Platform)> workspaceConnections = await connections.ListAsync(workspaceId);
        if (!workspaceConnections.Any(c => c.Id == connectionId))
        {
            throw new CheckoutRefusedException("connection_id isn't one of this business's conversations.");
        }
    }

    /// <summary>A line priced from the live catalog. Problem is set when it can't be bought as asked.</summary>
    private sealed record LineQuote(CheckoutItem Item, string Name, string? OptionName, long? UnitMinor, string? Problem);

    /// <summary>The lines to save, when every one of them can be bought; otherwise every problem at once, so the agent can fix them all.</summary>
    private static List<PaymentOrderLine> Agreed(List<LineQuote> quotes)
    {
        List<string> problems = quotes.Where(q => q.Problem is not null).Select(q => q.Problem!).ToList();
        if (problems.Count > 0)
        {
            throw new CheckoutRefusedException(string.Join(" ", problems) + " Sort these out with the customer, then try again.");
        }

        return quotes.Select(q => new PaymentOrderLine(q.Item.ProductId, q.Item.VariantId, q.Name, q.OptionName, q.Item.Quantity,
            q.UnitMinor!.Value, q.UnitMinor.Value * q.Item.Quantity, q.UnitMinor.Value)).ToList();
    }

    /// <summary>
    /// Each item priced from the live catalog, with what's wrong with it if anything. countStock
    /// false: the order's own stock is already kept aside, so how many are left isn't checked.
    /// Throws refused for an item that isn't on sale or a wrong option, which no customer can fix.
    /// </summary>
    private async Task<List<LineQuote>> QuoteAsync(string workspaceId, List<CheckoutItem> items, bool countStock, CancellationToken ct)
    {
        Guid[] ids = items.Select(i => i.ProductId).Distinct().ToArray();
        CatalogPrices prices;
        try
        {
            JsonElement answer = await catalog.CallAsync(workspaceId, "get_prices", new { Ids = ids }, ct);
            prices = answer.GetProperty("result").Deserialize<CatalogPrices>(CatalogJson)
                ?? throw new JsonException("get_prices answered with no result.");
        }
        catch (CatalogToolException ex) when (ex.Refused)
        {
            throw new CheckoutRefusedException(ex.Message);
        }
        catch (CatalogToolException ex)
        {
            throw new CheckoutUnavailableException($"{ex.Message} Tell the customer you'll send the checkout link shortly.");
        }

        Dictionary<Guid, CatalogItemPrice> byId = (prices.Items ?? []).Concat(prices.Closest ?? []).ToDictionary(i => i.ProductId);
        List<string> wrong = [];
        List<LineQuote> quotes = [];

        foreach (CheckoutItem item in items)
        {
            if (!byId.TryGetValue(item.ProductId, out CatalogItemPrice? product))
            {
                wrong.Add($"{item.ProductId} isn't on sale.");
                continue;
            }

            string name = product.Name.Value;
            string? optionName = null;
            decimal? price;
            bool available;
            int? left;

            List<CatalogVariantPrice> variants = product.Variants ?? [];
            if (variants.Count > 0)
            {
                if (item.VariantId is null)
                {
                    wrong.Add($"{name} comes in options ({string.Join(", ", variants.Select(v => v.Name.Value))}): ask which one and pass its variant_id.");
                    continue;
                }

                CatalogVariantPrice? option = variants.FirstOrDefault(v => v.VariantId == item.VariantId);
                if (option is null)
                {
                    wrong.Add($"{item.VariantId} isn't one of {name}'s options.");
                    continue;
                }

                (optionName, price, available, left) = (option.Name.Value, option.Price, option.Available, option.QuantityLeft);
            }
            else
            {
                if (item.VariantId is not null)
                {
                    wrong.Add($"{name} has no options: leave variant_id out.");
                    continue;
                }

                (price, available, left) = (product.Price, product.Available, product.QuantityLeft);
            }

            if ((product.Currency ?? Currency) != Currency)
            {
                throw new CheckoutRefusedException($"Checkouts can only be made in {Currency}. Tell the customer the business will send payment details.");
            }

            string label = optionName is null ? name : $"{name} ({optionName})";
            string? problem = !available && countStock ? $"{label} is sold out."
                : price is null ? $"{label} is priced on request: the business has to quote it."
                : countStock && left is { } count && item.Quantity > count ? $"Only {count} {label} left."
                : null;
            quotes.Add(new LineQuote(item, name, optionName, price is null ? null : ToMinor(price.Value), problem));
        }

        if (wrong.Count > 0)
        {
            throw new CheckoutRefusedException(string.Join(" ", wrong) + " Sort these out with the customer, then try again.");
        }

        return quotes;
    }

    /// <summary>What the customer sees on the payment page: the business, then what it's for.</summary>
    private static Task<string> DescribeAsync(Workspace? workspace, List<PaymentOrderLine> lines)
    {
        string? business = workspace?.Name;
        StringBuilder text = new (Plain(string.IsNullOrWhiteSpace(business) ? "Your order" : business!.Trim()));
        text.Append(": ");
        text.Append(string.Join(", ", lines.Select(l => $"{l.Quantity} x {Plain(l.VariantName is null ? l.Name : $"{l.Name} ({l.VariantName})")}")));
        return Task.FromResult(text.Length <= MaxDescription ? text.ToString() : text.ToString(0, MaxDescription - 3) + "...");
    }

    /// <summary>Printable text only: names are typed by the business.</summary>
    private static string Plain(string value) => new (value.Where(c => !char.IsControl(c)).ToArray());

    private static string Required(string? value, int max, string field)
    {
        string trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > max)
        {
            throw new CheckoutRefusedException($"Pass {field} (1 to {max} characters).");
        }

        return trimmed;
    }

    private static string? Optional(string? value, int max)
    {
        string? trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return trimmed is { Length: > 0 } && trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private sealed record CatalogPrices(List<CatalogItemPrice>? Items, List<CatalogItemPrice>? Closest);

    private sealed record CatalogItemPrice(
        Guid ProductId,
        CatalogText Name,
        bool Available,
        decimal? Price,
        string? Currency,
        int? QuantityLeft,
        List<CatalogVariantPrice>? Variants);

    private sealed record CatalogVariantPrice(Guid VariantId, CatalogText Name, decimal? Price, bool Available, int? QuantityLeft);

    private sealed record CatalogText(string Value);
}
