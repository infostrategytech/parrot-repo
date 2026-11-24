using System.Text;
using System.Text.RegularExpressions;
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
public sealed partial class CheckoutService(
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
    /// <summary>A new cart: send the customer its link.</summary>
    public const string CartReady = "cart_ready";

    /// <summary>The conversation's open cart, changed: same link, new items.</summary>
    public const string CartUpdated = "cart_updated";

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
        WorkspacePaymentSettings rules = await workspaceSettings.ResolveAsync(workspaceId, ct);

        // The conversation's cart, while the customer hasn't pressed Pay: changed in place, so the link
        // they already have shows the new items. Read again if something changed it underneath.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            PaymentOrder? cart = await orders.FindOpenCartAsync(workspaceId, request.ConnectionId, sender, Now, ct);
            if (cart is null)
            {
                break;
            }

            if (await TryChangeCartAsync(cart, request, items, rules, key, ct) is { } changed)
            {
                return changed;
            }
        }

        return await NewCartAsync(workspaceId, request with { Sender = sender, IdempotencyKey = key }, items, rules, ct);
    }

    /// <summary>A new cart for the conversation, with its stock kept aside when the business asks; any older open cart is replaced.</summary>
    private async Task<CheckoutResult> NewCartAsync(string workspaceId, CreateCheckoutRequest request, List<CheckoutItem> items, WorkspacePaymentSettings rules, CancellationToken ct)
    {
        string key = request.IdempotencyKey;
        string sender = request.Sender;
        List<PaymentOrderLine> lines = Agreed(await QuoteAsync(workspaceId, items, countStock: true, ct));
        long total = lines.Sum(l => l.LineTotalMinor);
        CheckLimit(total);

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
            Reference = NewReference(),
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
            try
            {
                await stock.HoldAsync(workspaceId, order.Reference, HeldUntil(checkoutExpires), StockLines(lines), ct);
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

        logger.LogInformation("Cart made. Workspace={Workspace} Order={Order} Agent={Agent} TotalMinor={Total} Hold={Hold} Replaced={Replaced}",
            workspaceId, order.Id, order.AgentId, total, order.HoldMode, replaced.Count);
        return Result(order);
    }

    /// <summary>
    /// The open cart given the agent's new list: priced again, its time started again, and its stock moved
    /// when it's kept aside. Null when the cart changed underneath (Pay pressed, another change saved).
    /// </summary>
    private async Task<CheckoutResult?> TryChangeCartAsync(PaymentOrder cart, CreateCheckoutRequest request, List<CheckoutItem> items, WorkspacePaymentSettings rules, string key, CancellationToken ct)
    {
        // Stock the cart keeps aside is its own, so it isn't counted against it here; moving the hold checks what's left.
        List<PaymentOrderLine> lines = Agreed(await QuoteAsync(cart.WorkspaceId, items, countStock: cart.HoldMode != CheckoutHoldMode.AtCheckout, ct));
        long before = cart.TotalMinor;
        bool saved = await SaveCartAsync(cart, lines, lines.Sum(l => l.LineTotalMinor), Now.AddMinutes(rules.CheckoutMinutes), order =>
        {
            order.AgentId = request.AgentId;
            order.CustomerName = Optional(request.CustomerName, 120) ?? order.CustomerName;
            order.CustomerEmail = Optional(request.CustomerEmail, 254) ?? order.CustomerEmail;
            order.CustomerPhone = Optional(request.CustomerPhone, 32) ?? order.CustomerPhone;
            order.Note = Optional(request.Note, 500) ?? order.Note;
            order.LastChangeKey = key;
        }, ct);
        if (!saved)
        {
            return null;
        }

        logger.LogInformation("Cart changed. Workspace={Workspace} Order={Order} Agent={Agent} TotalMinor={Total} WasMinor={Was} Hold={Hold}",
            cart.WorkspaceId, cart.Id, cart.AgentId, cart.TotalMinor, before, cart.HoldMode);
        return Result(cart, changed: true);
    }

    /// <summary>
    /// Saves a cart's new lines over the cart as it was read: its stock moved first when it's kept aside, then
    /// the lines, totals and time, with anything else <paramref name="alsoSet"/> changes. False when the cart
    /// changed underneath (Pay pressed, another change saved), with any stock just kept for it let go again.
    /// </summary>
    private async Task<bool> SaveCartAsync(PaymentOrder cart, List<PaymentOrderLine> lines, long quotedTotal, DateTime expires, Action<PaymentOrder>? alsoSet, CancellationToken ct)
    {
        long total = lines.Sum(l => l.LineTotalMinor);
        CheckLimit(total);

        bool held = cart.HoldMode == CheckoutHoldMode.AtCheckout;
        DateTime readAt = cart.UpdatedAt;
        string reference = held ? await MoveHoldAsync(cart, lines, expires, readAt, ct) : cart.Reference;

        cart.Lines = lines;
        cart.TotalMinor = total;
        cart.QuotedTotalMinor = quotedTotal;
        cart.Reference = reference;
        cart.CheckoutExpiresAt = expires;
        cart.ExpiresAt = expires;
        cart.UpdatedAt = Now;
        alsoSet?.Invoke(cart);

        if (await orders.TryReplaceCartAsync(cart, readAt, CancellationToken.None))
        {
            return true;
        }

        if (held)
        {
            await ReleaseQuietlyAsync(cart.WorkspaceId, reference);
        }

        return false;
    }

    /// <summary>
    /// Moves a cart's kept-aside stock to its new lines, returning the reference it's now kept under. The catalog
    /// keeps a reference's hold once and for all, so the new hold takes a new reference: the provider only
    /// sees the reference once Pay is pressed, so it can still change. When the new lines can't be held the
    /// cart is left as it was, its old lines held again under a reference of their own (or, if even that
    /// can't be done, checked and held at Pay instead), and the change is refused.
    /// </summary>
    private async Task<string> MoveHoldAsync(PaymentOrder cart, List<PaymentOrderLine> lines, DateTime expires, DateTime readAt, CancellationToken ct)
    {
        // Let go first, so the cart's own stock counts as free for its new lines.
        await ReleaseQuietlyAsync(cart.WorkspaceId, cart.Reference);
        string moved = NewReference();
        try
        {
            await stock.HoldAsync(cart.WorkspaceId, moved, HeldUntil(expires), StockLines(lines), ct);
            return moved;
        }
        catch (Exception ex) when (ex is StockShortException or CatalogStockUnavailableException)
        {
            string back = NewReference();
            cart.Reference = back;
            try
            {
                await stock.HoldAsync(cart.WorkspaceId, back, HeldUntil(cart.ExpiresAt!.Value), StockLines(cart.Lines), CancellationToken.None);
            }
            catch (Exception again) when (again is StockShortException or CatalogStockUnavailableException)
            {
                cart.HoldMode = CheckoutHoldMode.AtPay;
                logger.LogWarning("A cart's stock couldn't be kept aside again; it's checked at Pay instead. Order={Order} Reason={Reason}", cart.Id, again.Message);
            }

            cart.UpdatedAt = Now;
            if (!await orders.TryReplaceCartAsync(cart, readAt, CancellationToken.None) && cart.HoldMode == CheckoutHoldMode.AtCheckout)
            {
                await ReleaseQuietlyAsync(cart.WorkspaceId, back);
            }

            throw ex is StockShortException
                ? new CheckoutRefusedException($"{ex.Message} The cart wasn't changed: the customer's link still shows what it had. Check get_prices and agree the change with the customer.")
                : new CheckoutUnavailableException("Stock can't be checked right now, so the cart wasn't changed. Tell the customer you'll update it shortly.");
        }
    }

    private static string NewReference() => "prt_" + Guid.NewGuid().ToString("N");

    /// <summary>Kept until the latest a payment started at the cart's last minute could run; the sweep lets it go as soon as the cart or its payment lapses.</summary>
    private static DateTime HeldUntil(DateTime cartExpires) => cartExpires.AddMinutes(WorkspacePaymentSettingsService.MaxPaymentMinutes);

    // ---- The agent: adding to and taking out of the cart --------------------------------

    /// <summary>
    /// Adds items to the conversation's open cart (more of one already in it goes on its line), keeping its link; with no
    /// open cart, makes one of just these. Priced from the live catalog like create_cart. Refused, the cart unchanged,
    /// while the customer is paying: a change then needs a new cart with the complete list.
    /// </summary>
    public Task<CheckoutResult> AddToCartAsync(string workspaceId, ChangeCartRequest request, CancellationToken ct) =>
        ChangeCartAsync(workspaceId, request, adding: true, ct);

    /// <summary>
    /// Takes items out of the conversation's open cart, or lowers how many, keeping its link. Refused when there's no
    /// open cart, an item isn't in it, or it would leave the cart empty.
    /// </summary>
    public Task<CheckoutResult> RemoveFromCartAsync(string workspaceId, ChangeCartRequest request, CancellationToken ct) =>
        ChangeCartAsync(workspaceId, request, adding: false, ct);

    private async Task<CheckoutResult> ChangeCartAsync(string workspaceId, ChangeCartRequest request, bool adding, CancellationToken ct)
    {
        await EnsureOnAsync(ct);
        string key = Required(request.IdempotencyKey, MaxKey, "idempotency_key");
        string sender = Required(request.Sender, MaxSender, "sender");

        // The same add that made a new cart, sent again.
        if (await orders.FindByIdempotencyKeyAsync(workspaceId, key, ct) is { } earlier)
        {
            return Again(earlier);
        }

        List<CartItemChange> changes = Changes(request.Items, adding);
        await CheckConversationAsync(workspaceId, request.AgentId, request.ConnectionId);
        WorkspacePaymentSettings rules = await workspaceSettings.ResolveAsync(workspaceId, ct);
        CreateCheckoutRequest asCart = new (request.AgentId, request.ConnectionId, sender, [], key,
            request.CustomerName, request.CustomerEmail, request.CustomerPhone, request.Note);

        // Each go reads the cart afresh and makes the change to what it holds now, so two changes at once both land.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            PaymentOrder? cart = await orders.FindOpenCartAsync(workspaceId, request.ConnectionId, sender, Now, ct);
            if (cart is null)
            {
                return adding
                    ? await FirstCartAsync(workspaceId, asCart, changes, rules, ct)
                    : throw new CheckoutRefusedException(await NoOpenCartAsync(workspaceId, request.ConnectionId, sender, "there's nothing to take out", ct));
            }

            // The same change, sent again: it's already in the cart.
            if (cart.LastChangeKey == key)
            {
                return Result(cart, changed: true);
            }

            List<CheckoutItem> next = adding ? Added(cart, changes) : Removed(cart, changes);
            if (await TryChangeCartAsync(cart, asCart, next, rules, key, ct) is { } changed)
            {
                return changed;
            }
        }

        throw new CheckoutUnavailableException("The cart kept changing while this was being saved. Call get_cart to see what's in it now, then try again with a new idempotency_key.");
    }

    /// <summary>add_to_cart with no open cart: a new one of just these items, unless the customer is paying the last one.</summary>
    private async Task<CheckoutResult> FirstCartAsync(string workspaceId, CreateCheckoutRequest request, List<CartItemChange> changes, WorkspacePaymentSettings rules, CancellationToken ct)
    {
        if (await orders.FindLatestCartAsync(workspaceId, request.ConnectionId, request.Sender, ct) is { } latest && Paying(latest))
        {
            throw new CheckoutRefusedException(PayingNow("add to it"));
        }

        return await NewCartAsync(workspaceId, request, Merge(changes.Select(c => new CheckoutItem(c.ProductId, c.VariantId, c.Quantity!.Value)).ToList()), rules, ct);
    }

    /// <summary>Why remove_from_cart found no open cart, worded for the agent.</summary>
    private async Task<string> NoOpenCartAsync(string workspaceId, Guid connectionId, string sender, string so, CancellationToken ct) =>
        await orders.FindLatestCartAsync(workspaceId, connectionId, sender, ct) is { } latest && Paying(latest)
            ? PayingNow("take anything out")
            : $"This conversation has no open cart, so {so}. Call get_cart to see where the customer's last cart got to.";

    /// <summary>The customer pressed Pay and the payment can still arrive: the cart is theirs now and can't change.</summary>
    private bool Paying(PaymentOrder order) =>
        order.PaidAt is null && (order.Status == PaymentOrderStatus.Creating || (order.Status == PaymentOrderStatus.Pending && order.ExpiresAt > Now));

    private static string PayingNow(string what) =>
        $"The customer has already pressed Pay on their cart, so you can't {what} now. If they want a different order, agree the complete list with them "
        + "and call create_cart, which makes a new cart and link in place of the one being paid.";

    /// <summary>What add_to_cart or remove_from_cart was asked, checked: real ids, and quantities that make sense for the change.</summary>
    private static List<CartItemChange> Changes(IReadOnlyList<CartItemChange>? items, bool adding)
    {
        if (items is not { Count: > 0 })
        {
            throw new CheckoutRefusedException(adding
                ? "Pass at least one item to add: each with product_id, variant_id when the product has options, and quantity."
                : "Pass at least one item to take out: each with product_id, and quantity to take out only some of it.");
        }

        if (items.Count > MaxItems)
        {
            throw new CheckoutRefusedException($"A cart can have at most {MaxItems} different items.");
        }

        foreach (CartItemChange item in items)
        {
            if (item.ProductId == Guid.Empty || item.VariantId == Guid.Empty)
            {
                throw new CheckoutRefusedException("Each item needs a real product_id (and variant_id, when given).");
            }

            if (adding && item.Quantity is null)
            {
                throw new CheckoutRefusedException("Say how many to add (quantity, at least 1) for each item.");
            }

            if (item.Quantity is < 1 or > MaxQuantity)
            {
                throw new CheckoutRefusedException($"Each quantity must be between 1 and {MaxQuantity}.");
            }
        }

        return [.. items];
    }

    /// <summary>The cart with the items added: more of one already in it goes on its line.</summary>
    private static List<CheckoutItem> Added(PaymentOrder cart, List<CartItemChange> changes) =>
        Merge([.. Items(cart), .. changes.Select(c => new CheckoutItem(c.ProductId, c.VariantId, c.Quantity!.Value))]);

    /// <summary>The cart with the items taken out or lowered. Every item named must be in it, and something must be left.</summary>
    private static List<CheckoutItem> Removed(PaymentOrder cart, List<CartItemChange> changes)
    {
        List<CheckoutItem> items = Items(cart);
        List<string> wrong = [];
        foreach (CartItemChange change in changes)
        {
            List<CheckoutItem> matches = items.Where(i => i.ProductId == change.ProductId && (change.VariantId is null || i.VariantId == change.VariantId)).ToList();
            if (matches.Count == 0)
            {
                wrong.Add(change.VariantId is null ? $"{change.ProductId} isn't in the cart." : $"{change.ProductId} with option {change.VariantId} isn't in the cart.");
                continue;
            }

            if (matches.Count > 1)
            {
                string name = cart.Lines.First(l => l.ProductId == change.ProductId).Name;
                wrong.Add($"The cart has {name} in more than one option ({string.Join(", ", cart.Lines.Where(l => l.ProductId == change.ProductId).Select(l => $"{l.VariantName}: {l.VariantId}"))}): pass the variant_id of the one to take out.");
                continue;
            }

            CheckoutItem line = matches[0];
            int index = items.IndexOf(line);
            if (change.Quantity is int fewer && fewer < line.Quantity)
            {
                items[index] = line with { Quantity = line.Quantity - fewer };
            }
            else
            {
                items.RemoveAt(index);
            }
        }

        if (wrong.Count > 0)
        {
            throw new CheckoutRefusedException(string.Join(" ", wrong) + " The cart wasn't changed. Call get_cart to see what's in it.");
        }

        if (items.Count == 0)
        {
            throw new CheckoutRefusedException("That would leave the cart empty, so it wasn't changed. If the customer doesn't want anything any more, tell them "
                + "there's nothing to pay: the cart runs out unpaid at expires_at. To swap what's in it, call create_cart with the complete new list.");
        }

        return items;
    }

    // ---- The agent: what's in the cart ---------------------------------------------------

    /// <summary>
    /// A cart as it stands: the one an order id (or prt_ reference) names, or else the conversation's newest.
    /// While it's open each item is priced again from the catalog, exactly as the customer's page shows it.
    /// </summary>
    public async Task<CartStatus> GetCartAsync(string workspaceId, string? orderIdOrReference, Guid? connectionId, string? sender, CancellationToken ct)
    {
        PaymentOrder? order;
        if (!string.IsNullOrWhiteSpace(orderIdOrReference))
        {
            Match key = OrderKey().Match(orderIdOrReference);
            order = !key.Success ? null
                : key.Groups["id"].Success ? await orders.FindAsync(workspaceId, Guid.Parse(key.Groups["id"].Value), ct)
                : await orders.FindByReferenceAsync(key.Groups["ref"].Value, ct) is { } byReference && byReference.WorkspaceId == workspaceId ? byReference
                : null;
            if (order is null)
            {
                throw new CheckoutRefusedException("There's no such cart for this business. Pass the order_id create_cart returned, or leave it out and pass connection_id and sender for the conversation's cart.");
            }
        }
        else if (connectionId is { } connection && connection != Guid.Empty && !string.IsNullOrWhiteSpace(sender))
        {
            order = await orders.FindLatestCartAsync(workspaceId, connection, sender.Trim(), ct)
                ?? throw new CheckoutRefusedException("This conversation has no cart yet. Once the customer has agreed what they want, make one with create_cart.");
        }
        else
        {
            throw new CheckoutRefusedException("Pass order_id, or connection_id and sender for the conversation's cart.");
        }

        CheckoutView view = await ViewAsync(order, ct);

        // The page lists the order's lines in order, so each one's ids are the order line's at the same place.
        CartLine[] items = view.Lines.Select((l, i) => new CartLine(order.Lines[i].ProductId, order.Lines[i].VariantId, l.Name, l.VariantName,
            l.Quantity, l.QuotedUnitPrice, l.UnitPrice, l.LineTotal, l.PriceChanged, l.Problem)).ToArray();
        bool open = view.State == CheckoutState.Open;
        string guidance = view.State switch
        {
            CheckoutState.Open when !view.CanPay => $"The customer can't pay this cart as it stands: {view.Message} Agree a change with them, then call add_to_cart or remove_from_cart, or create_cart with the complete new list.",
            CheckoutState.Open when view.PriceChanged => "The cart is open, but some prices changed since it was made: tell the customer the new total before they pay. "
                + "To change the cart, call add_to_cart or remove_from_cart, or create_cart with the complete new list; send checkout_url again only if they lost it.",
            CheckoutState.Open => "The cart is open and can be paid. To change it, call add_to_cart or remove_from_cart, or create_cart with the complete new list; send checkout_url again only if they lost it.",
            CheckoutState.Paying => "The customer pressed Pay and is paying. Don't change the cart unless they ask: a change now makes a new cart and link. Use get_payment_status to see whether it's paid.",
            CheckoutState.Paid => "This cart has been paid. Use get_payment_status for where the order is.",
            CheckoutState.Replaced => "A newer cart replaced this one. Call get_cart with connection_id and sender for the conversation's current cart.",
            CheckoutState.Unavailable => "This cart couldn't be paid. If the customer still wants the items, call create_cart again.",
            _ => "This cart's time ran out unpaid. If the customer still wants the items, check get_prices and call create_cart again.",
        };

        return new CartStatus(order.Id, view.State, items, view.Total, view.QuotedTotal, view.Currency, view.PriceChanged,
            open && view.CanPay, open ? view.Message : null,
            open || view.State == CheckoutState.Paying ? links.For(order) : null, view.ExpiresAt, guidance);
    }

    // An order id (UUID) or a reference (prt_ and 32 hex), anywhere in the text: agents wrap ids in quotes or JSON.
    [GeneratedRegex(@"(?<id>\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b)|(?<ref>\bprt_[0-9a-f]{32}\b)")]
    private static partial Regex OrderKey();

    // ---- The customer: the checkout page ------------------------------------------------

    /// <summary>The checkout a link names, priced live while it's open.</summary>
    public async Task<CheckoutView> GetAsync(string token, CancellationToken ct)
    {
        PaymentOrder order = await FindAsync(token, ct);
        return await ViewAsync(order, ct);
    }

    /// <summary>
    /// The customer changed their cart on the page: a quantity up or down, or an item taken out. Only items already in
    /// it, each up to what's left; whatever isn't listed is taken out, and at least one item stays. Each item is priced
    /// again, keeping what the business quoted beside it, so a price that moved still shows. The cart's time isn't
    /// restarted: that's the business's to give. Refused, with the checkout as it is now, if it can't be changed.
    /// </summary>
    public async Task<CheckoutView> ChangeAsync(string token, ChangeCheckoutRequest request, CancellationToken ct)
    {
        PaymentOrder order = await FindAsync(token, ct);
        if (order.Status != PaymentOrderStatus.AwaitingCheckout || order.ExpiresAt <= Now)
        {
            CheckoutView closed = await ViewAsync(order, ct);
            throw new CheckoutChangedException(closed.State == CheckoutState.Paying
                ? "You've already started paying, so this order can't be changed. Message the business if it isn't right."
                : closed.Message ?? "This checkout can't be changed any more.", closed);
        }

        await EnsureOnAsync(ct);
        // A quantity of 0 takes the item out, as leaving it out does.
        List<CheckoutItem> kept = (request.Items ?? []).Where(i => i.Quantity != 0).ToList();
        if (kept.Count == 0)
        {
            throw new CheckoutChangedException("Keep at least one item. If you don't want the order any more, you don't need to pay.", await ViewAsync(order, ct));
        }

        List<CheckoutItem> wanted;
        try
        {
            wanted = Merge(kept);
        }
        catch (CheckoutRefusedException)
        {
            throw new CheckoutChangedException($"Choose between 1 and {MaxQuantity} of each item.", await ViewAsync(order, ct));
        }

        if (wanted.Any(i => !order.Lines.Any(l => l.ProductId == i.ProductId && l.VariantId == i.VariantId)))
        {
            throw new CheckoutChangedException("Only items already in your order can be changed here. Message the business to add something.", await ViewAsync(order, ct));
        }

        List<LineQuote> quotes;
        List<PaymentOrderLine> lines;
        try
        {
            quotes = await QuoteAsync(order.WorkspaceId, wanted, countStock: order.HoldMode != CheckoutHoldMode.AtCheckout, ct);
            lines = Agreed(quotes);
        }
        catch (CheckoutRefusedException ex)
        {
            throw new CheckoutChangedException(ForCustomer(ex.Message), await ViewAsync(order, ct));
        }

        // Each line keeps the business's quote, so what moved since still shows.
        lines = lines.Select(l => l with { QuotedUnitPriceMinor = order.Lines.First(o => o.ProductId == l.ProductId && o.VariantId == l.VariantId).QuotedUnitMinor }).ToList();
        long quotedTotal = lines.Sum(l => l.QuotedUnitMinor * l.Quantity);
        bool saved;
        try
        {
            saved = await SaveCartAsync(order, lines, quotedTotal, order.ExpiresAt!.Value, null, ct);
        }
        catch (CheckoutRefusedException ex)
        {
            throw new CheckoutChangedException(ForCustomer(ex.Message), await GetAsync(token, ct));
        }

        if (!saved)
        {
            throw new CheckoutChangedException("Your order changed while you were editing it. Check it, then try again.", await GetAsync(token, ct));
        }

        logger.LogInformation("Cart changed by the customer. Workspace={Workspace} Order={Order} TotalMinor={Total} Lines={Lines}",
            order.WorkspaceId, order.Id, order.TotalMinor, order.Lines.Count);
        return await ViewAsync(order, ct);
    }

    /// <summary>A refusal worded for the agent, without its advice to the agent, for the customer's page.</summary>
    private static string ForCustomer(string message)
    {
        foreach (string agentOnly in new[] { " Sort these out with the customer, then try again.", " The cart wasn't changed: the customer's link still shows what it had. Check get_prices and agree the change with the customer." })
        {
            message = message.Replace(agentOnly, "", StringComparison.Ordinal);
        }

        return message.Trim() + " Your order hasn't changed.";
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
            // After paying, Accelerate sends the customer back to this checkout, which waits for the webhook.
            link = await provider.CreateLinkAsync(starting.Reference, total, description, links.ReturnFor(starting), ct);
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
                lineChanged, quote?.Problem, line.ProductId, line.VariantId, MostFor(order, line, quote)));
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

    /// <summary>
    /// The most of a line the customer can have: what's left, plus what this cart already keeps aside of it. Uncounted
    /// stock has no limit but the checkout's own; a line that can't be priced now can only go down.
    /// </summary>
    private static int MostFor(PaymentOrder order, PaymentOrderLine line, LineQuote? quote) =>
        quote?.UnitMinor is null ? line.Quantity
        : quote.Left is not int left ? MaxQuantity
        : Math.Clamp(left + (order.HoldMode == CheckoutHoldMode.AtCheckout ? line.Quantity : 0), 0, MaxQuantity);

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

        if (paying && !provider.IsConfigured)
        {
            logger.LogError("Pay was pressed but the payment provider isn't configured.");
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
        if (order.HoldMode != CheckoutHoldMode.Never)
        {
            await ReleaseQuietlyAsync(order.WorkspaceId, order.Reference);
        }
    }

    private async Task ReleaseQuietlyAsync(string workspaceId, string reference)
    {
        try
        {
            await stock.ReleaseAsync(workspaceId, reference, CancellationToken.None);
        }
        catch (CatalogStockUnavailableException ex)
        {
            logger.LogWarning("Stock hold not released; it will run out at its time. Reference={Reference} Reason={Reason}", reference, ex.Message);
        }
    }

    /// <summary>The answer to a request already seen: the same checkout, or why there isn't one.</summary>
    private CheckoutResult Again(PaymentOrder order) => order.Status switch
    {
        PaymentOrderStatus.AwaitingCheckout or PaymentOrderStatus.Pending or PaymentOrderStatus.Creating => Result(order),
        PaymentOrderStatus.Failed => throw new CheckoutUnavailableException("That cart couldn't be made. Try again with a new idempotency_key."),
        PaymentOrderStatus.Expired => throw new CheckoutRefusedException("That cart expired. If the customer still wants the items, check get_prices and call create_cart with a new idempotency_key."),
        PaymentOrderStatus.Cancelled => throw new CheckoutRefusedException("That cart was replaced by a newer one for this conversation. Send the newer link."),
        _ => throw new CheckoutRefusedException("That cart has been paid. Use get_payment_status for where it is."),
    };

    private CheckoutResult Result(PaymentOrder order, bool changed = false) => new (
        order.Id,
        changed ? CartUpdated : CartReady,
        links.For(order),
        FromMinor(order.TotalMinor),
        order.Currency,
        order.Lines.Select(l => new CheckoutLine(l.ProductId, l.VariantId, l.Name, l.VariantName, l.Quantity, FromMinor(l.UnitPriceMinor), FromMinor(l.LineTotalMinor))).ToArray(),
        order.CheckoutExpiresAt,
        changed
            ? "The customer's cart now holds exactly these items, and checkout_url is the same link they already have. "
              + "Tell them what changed and the new total; send the link again only if they've lost it. Its time started again (expires_at). "
              + "Don't tell the customer the order is paid: use get_payment_status."
            : "Send the customer checkout_url with the total and what it's for. On that page they see the latest prices and press Pay, which takes them to the payment page. "
              + "This is the conversation's cart: to change it, call add_to_cart or remove_from_cart, or create_cart with the complete new list, and the same link shows the change. "
              + "If a price changes before they pay, the page shows the old and the new price. "
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
    private sealed record LineQuote(CheckoutItem Item, string Name, string? OptionName, long? UnitMinor, string? Problem, int? Left = null);

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
            quotes.Add(new LineQuote(item, name, optionName, price is null ? null : ToMinor(price.Value), problem, available ? left : 0));
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
