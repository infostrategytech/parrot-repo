using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.Email;
using Parrot.Application.Payments;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Test.Parrot.Api.Tests.Services.Teams;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Services.Payments;

/// <summary>A clock the test moves on.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new (2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

// The checkout an agent makes and the customer pays from: priced from the catalog, never from the
// agent; the payment link only made when the customer presses Pay; a price change shown and agreed
// before it's charged; stock kept aside when the workspace says; and every order in its own workspace.
public sealed class CheckoutServiceTests
{
    private const string Ws = "ws-1";
    private const string Sender = "2348012345678";

    private readonly FakePaymentOrderRepository _orders = new();
    private readonly FakePaymentProvider _provider = new();
    private readonly FakePriceCatalog _catalog = new();
    private readonly FakeStockClient _stock = new();
    private readonly FakeConnectionDirectory _connections = new();
    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeFlags _flags = new();
    private readonly FakeWorkspacePaymentSettingsRepository _rules = new();
    private readonly Mock<IAgentRepository> _agents = new();
    private readonly PaymentSettings _settings = new() { CallbackUrl = "https://app.example/paid", MaxLinkAmount = 100_000m, CheckoutMinutes = 30, LinkMinutes = 15 };
    private readonly TestClock _clock = new();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _otherAgent = Guid.NewGuid();
    private readonly Guid _whatsApp;
    private readonly CheckoutLinks _links;
    private readonly CheckoutService _service;

    public CheckoutServiceTests()
    {
        _workspaces.Seed(Ws, "owner", name: "Ada's Bags");
        _workspaces.Seed("ws-2", "owner");
        _whatsApp = _connections.Add(Ws, MessagingPlatform.WhatsApp);
        _agents.Setup(a => a.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Guid id) =>
            id == _agent ? new Agent { Id = id, WorkspaceId = Ws, Name = "Ngozi", Description = "", MascotUrl = "" }
            : id == _otherAgent ? new Agent { Id = id, WorkspaceId = "ws-2", Name = "Other", Description = "", MascotUrl = "" }
            : null);
        _links = Links(_settings, "a-secret-long-enough-for-hmac-sha256-signing!!");
        WorkspacePaymentSettingsService rules = new (_rules, Options.Create(_settings), _clock);
        _service = new CheckoutService(_orders, _provider, _catalog, _stock, _agents.Object, _connections, _workspaces, _flags,
            rules, _links, Options.Create(_settings), _clock, NullLogger<CheckoutService>.Instance);
    }

    public static CheckoutLinks Links(PaymentSettings settings, string secret) => new (
        new CheckoutTokens(Options.Create(new JwtSettings { Secret = secret, Issuer = "parrot", Audience = "parrot-api" })),
        Options.Create(settings),
        Options.Create(new EmailSettings { SmtpHost = "smtp", SmtpUser = "u", SmtpPassword = "p", FromAddress = "a@b.c", FromName = "yára", BaseUrl = "https://app.example/" }));

    private CreateCheckoutRequest Request(string key = "key-1", params CheckoutItem[] items) =>
        new(_agent, _whatsApp, Sender, items, key, CustomerName: "Kemi", Note: "Deliver to Lekki");

    private static CheckoutItem Item(Guid product, int quantity = 1, Guid? option = null) => new(product, option, quantity);

    private static string TokenOf(CheckoutResult checkout) => checkout.CheckoutUrl[(checkout.CheckoutUrl.LastIndexOf('/') + 1)..];

    // ---- Making a checkout ------------------------------------------------------------

    [Fact]
    public async Task A_checkout_is_priced_from_the_catalog_saved_and_no_payment_link_is_made_yet()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        (Guid basket, Guid[] sizes) = _catalog.WithOptions("Woven basket", ("Small", 9000m, true, 3), ("Large", 15000m, true, null));

        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2), Item(basket, 1, sizes[1])), default);

        Assert.Equal(CheckoutService.CartReady, checkout.Status);
        Assert.Equal(23000m, checkout.Total);
        Assert.StartsWith("https://app.example/checkout/", checkout.CheckoutUrl);
        Assert.Collection(checkout.Lines,
            line => Assert.Equal(("Quilted bag", (string?)null, 2, 4000m, 8000m), (line.Name, line.VariantName, line.Quantity, line.UnitPrice, line.LineTotal)),
            line => Assert.Equal(("Woven basket", (string?)"Large", 1, 15000m, 15000m), (line.Name, line.VariantName, line.Quantity, line.UnitPrice, line.LineTotal)));
        Assert.Empty(_provider.Calls);
        Assert.Empty(_stock.Held);

        PaymentOrder order = Assert.Single(_orders.Orders);
        Assert.Equal(PaymentOrderStatus.AwaitingCheckout, order.Status);
        Assert.Equal((2_300_000L, 2_300_000L), (order.TotalMinor, order.QuotedTotalMinor));
        Assert.Equal(_clock.Now.UtcDateTime.AddMinutes(30), order.CheckoutExpiresAt);
        Assert.Equal(order.CheckoutExpiresAt, order.ExpiresAt);
        Assert.Equal(CheckoutHoldMode.AtPay, order.HoldMode);
        Assert.Equal((_whatsApp, Sender, "Kemi", "Deliver to Lekki"), (order.ConnectionId, order.CustomerSender, order.CustomerName, order.Note));

        // The link names this order in this workspace, and nothing else.
        Assert.Equal((order.Id, Ws), _links.Read(TokenOf(checkout)));
    }

    [Fact]
    public async Task The_same_request_twice_returns_the_same_checkout_link()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);

        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);
        CheckoutResult again = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);

        Assert.Equal(first.OrderId, again.OrderId);
        Assert.Equal(first.CheckoutUrl, again.CheckoutUrl);
        Assert.Single(_orders.Orders);
    }

    // ---- The conversation's cart -------------------------------------------------------

    [Fact]
    public async Task Calling_again_changes_the_open_cart_in_place_and_keeps_its_link()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        Guid purse = _catalog.Product("Beaded purse", 2500m);
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag), Item(purse)), default);
        _clock.Advance(TimeSpan.FromMinutes(20));

        // The complete new cart: three bags, and the purse left out.
        CheckoutResult changed = await _service.CreateAsync(Ws, Request("key-2", Item(bag, 3)), default);

        Assert.Equal(CheckoutService.CartUpdated, changed.Status);
        Assert.Equal(first.OrderId, changed.OrderId);
        Assert.Equal(_links.Read(TokenOf(first)), _links.Read(TokenOf(changed)));
        Assert.Equal(12000m, changed.Total);
        CheckoutLine line = Assert.Single(changed.Lines);
        Assert.Equal(("Quilted bag", 3), (line.Name, line.Quantity));

        PaymentOrder order = Assert.Single(_orders.Orders);
        Assert.Equal((PaymentOrderStatus.AwaitingCheckout, 1_200_000L, 1_200_000L), (order.Status, order.TotalMinor, order.QuotedTotalMinor));
        Assert.Equal(_clock.Now.UtcDateTime.AddMinutes(30), order.ExpiresAt);
        Assert.Equal("key-1", order.IdempotencyKey);

        // The link the customer already has shows the new cart, after its first time would have run out; and a late retry of the first call doesn't undo the change.
        _clock.Advance(TimeSpan.FromMinutes(15));
        CheckoutView page = await _service.GetAsync(TokenOf(first), default);
        Assert.Equal((CheckoutState.Open, 12000m, 1), (page.State, page.Total, page.Lines.Count));
        Assert.Equal(12000m, (await _service.CreateAsync(Ws, Request("key-1", Item(bag), Item(purse)), default)).Total);
    }

    [Fact]
    public async Task A_change_keeps_the_customers_details_it_isnt_given()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);

        await _service.CreateAsync(Ws, new CreateCheckoutRequest(_agent, _whatsApp, Sender, [Item(bag, 2)], "key-2", CustomerEmail: "kemi@example.com"), default);

        PaymentOrder order = Assert.Single(_orders.Orders);
        Assert.Equal(("Kemi", "kemi@example.com", "Deliver to Lekki"), (order.CustomerName, order.CustomerEmail, order.Note));
    }

    [Fact]
    public async Task A_change_that_cant_be_bought_is_refused_and_the_cart_stays_as_it_was()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        Guid hat = _catalog.Product("Raffia hat", 3000m, available: false);
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);

        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("key-2", Item(bag), Item(hat)), default));

        PaymentOrder order = Assert.Single(_orders.Orders);
        Assert.Equal((first.OrderId, 400_000L, 1), (order.Id, order.TotalMinor, order.Lines.Count));
    }

    [Fact]
    public async Task Once_pay_is_pressed_a_new_cart_replaces_the_old_one_with_a_new_link()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);
        await _service.PayAsync(TokenOf(first), new PayCheckoutRequest(4000m), default);

        CheckoutResult second = await _service.CreateAsync(Ws, Request("key-2", Item(bag, 3)), default);

        Assert.Equal(CheckoutService.CartReady, second.Status);
        Assert.NotEqual(first.OrderId, second.OrderId);
        Assert.Equal(PaymentOrderStatus.Cancelled, _orders.Get(first.OrderId).Status);
        Assert.Equal(PaymentOrderStatus.AwaitingCheckout, _orders.Get(second.OrderId).Status);
        Assert.Equal(CheckoutState.Replaced, (await _service.GetAsync(TokenOf(first), default)).State);
        var replaced = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("key-1", Item(bag)), default));
        Assert.Contains("replaced by a newer one", replaced.Message);
    }

    [Fact]
    public async Task A_cart_whose_time_ran_out_isnt_changed_a_new_one_is_made()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);
        _clock.Advance(TimeSpan.FromMinutes(31));

        CheckoutResult second = await _service.CreateAsync(Ws, Request("key-2", Item(bag, 2)), default);

        Assert.Equal(CheckoutService.CartReady, second.Status);
        Assert.NotEqual(first.OrderId, second.OrderId);
        Assert.Equal(1, _orders.Get(first.OrderId).Lines.Single().Quantity);
    }

    [Fact]
    public async Task Another_customers_cart_is_their_own()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult kemi = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);

        CheckoutResult tunde = await _service.CreateAsync(Ws, new CreateCheckoutRequest(_agent, _whatsApp, "2348099999999", [Item(bag, 2)], "key-2"), default);

        Assert.NotEqual(kemi.OrderId, tunde.OrderId);
        Assert.Equal((PaymentOrderStatus.AwaitingCheckout, 1), (_orders.Get(kemi.OrderId).Status, _orders.Get(kemi.OrderId).Lines.Single().Quantity));
    }

    // ---- add_to_cart and remove_from_cart ---------------------------------------------

    private ChangeCartRequest Change(string key, params CartItemChange[] items) => new(_agent, _whatsApp, Sender, items, key);

    private static CartItemChange More(Guid product, int quantity = 1, Guid? option = null) => new(product, option, quantity);

    private static CartItemChange Less(Guid product, int? quantity = null, Guid? option = null) => new(product, option, quantity);

    [Fact]
    public async Task Adding_puts_items_in_the_open_cart_and_more_of_one_goes_on_its_line()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        Guid purse = _catalog.Product("Beaded purse", 2500m);
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);

        CheckoutResult added = await _service.AddToCartAsync(Ws, Change("key-2", More(purse, 2), More(bag)), default);

        Assert.Equal(CheckoutService.CartUpdated, added.Status);
        Assert.Equal(first.OrderId, added.OrderId);
        Assert.Equal(_links.Read(TokenOf(first)), _links.Read(TokenOf(added)));
        Assert.Equal(13000m, added.Total);
        Assert.Equal([("Quilted bag", 2), ("Beaded purse", 2)], added.Lines.Select(l => (l.Name, l.Quantity)));
        PaymentOrder order = Assert.Single(_orders.Orders);
        Assert.Equal((1_300_000L, "key-2"), (order.TotalMinor, order.LastChangeKey));
        Assert.Contains("add_to_cart or remove_from_cart", first.Guidance);
    }

    [Fact]
    public async Task Adding_with_no_cart_makes_one_and_a_retry_doesnt_make_a_second()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);

        CheckoutResult made = await _service.AddToCartAsync(Ws, Change("key-1", More(bag, 2)), default);
        CheckoutResult again = await _service.AddToCartAsync(Ws, Change("key-1", More(bag, 2)), default);

        Assert.Equal(CheckoutService.CartReady, made.Status);
        Assert.Equal((made.OrderId, 8000m), (again.OrderId, again.Total));
        Assert.Equal("key-1", Assert.Single(_orders.Orders).IdempotencyKey);
    }

    [Fact]
    public async Task The_same_change_sent_again_changes_nothing_more()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        Guid purse = _catalog.Product("Beaded purse", 2500m);
        await _service.CreateAsync(Ws, Request("key-1", Item(bag), Item(purse)), default);

        await _service.AddToCartAsync(Ws, Change("key-2", More(bag, 2)), default);
        CheckoutResult retried = await _service.AddToCartAsync(Ws, Change("key-2", More(bag, 2)), default);
        await _service.RemoveFromCartAsync(Ws, Change("key-3", Less(purse)), default);
        CheckoutResult removedAgain = await _service.RemoveFromCartAsync(Ws, Change("key-3", Less(purse)), default);

        Assert.Equal(3, retried.Lines.Single(l => l.Name == "Quilted bag").Quantity);
        Assert.Equal(("Quilted bag", 3, 12000m), (removedAgain.Lines.Single().Name, removedAgain.Lines.Single().Quantity, removedAgain.Total));
    }

    [Fact]
    public async Task Removing_takes_an_item_out_or_lowers_it_and_keeps_the_link()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        Guid purse = _catalog.Product("Beaded purse", 2500m);
        (Guid basket, Guid[] sizes) = _catalog.WithOptions("Woven basket", ("Small", 9000m, true, null), ("Large", 15000m, true, null));
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 3), Item(purse), Item(basket, 1, sizes[0])), default);

        // The basket is in the cart in one option only, so its variant_id can be left out.
        CheckoutResult changed = await _service.RemoveFromCartAsync(Ws, Change("key-2", Less(bag, 2), Less(purse), Less(basket)), default);

        Assert.Equal((CheckoutService.CartUpdated, first.OrderId), (changed.Status, changed.OrderId));
        Assert.Equal(("Quilted bag", 1, 4000m), (changed.Lines.Single().Name, changed.Lines.Single().Quantity, changed.Total));

        // Taking out more than there is takes the item out.
        await _service.AddToCartAsync(Ws, Change("key-3", More(purse)), default);
        CheckoutResult lowered = await _service.RemoveFromCartAsync(Ws, Change("key-4", Less(purse, 5)), default);
        Assert.Equal("Quilted bag", lowered.Lines.Single().Name);
    }

    [Fact]
    public async Task Removing_something_not_in_the_cart_an_unclear_option_or_everything_is_refused_and_nothing_changes()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        Guid hat = _catalog.Product("Raffia hat", 3000m);
        (Guid basket, Guid[] sizes) = _catalog.WithOptions("Woven basket", ("Small", 9000m, true, null), ("Large", 15000m, true, null));
        await _service.CreateAsync(Ws, Request("key-1", Item(bag), Item(basket, 1, sizes[0]), Item(basket, 1, sizes[1])), default);

        var missing = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.RemoveFromCartAsync(Ws, Change("key-2", Less(bag), Less(hat)), default));
        var unclear = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.RemoveFromCartAsync(Ws, Change("key-3", Less(basket)), default));
        var empty = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.RemoveFromCartAsync(Ws,
            Change("key-4", Less(bag), Less(basket, option: sizes[0]), Less(basket, option: sizes[1])), default));

        Assert.Contains($"{hat} isn't in the cart", missing.Message);
        Assert.Contains("more than one option", unclear.Message);
        Assert.Contains(sizes[1].ToString(), unclear.Message);
        Assert.Contains("leave the cart empty", empty.Message);
        PaymentOrder order = Assert.Single(_orders.Orders);
        Assert.Equal((3, 2_800_000L, (string?)null), (order.Lines.Count, order.TotalMinor, order.LastChangeKey));
    }

    [Fact]
    public async Task Adding_what_cant_be_bought_is_refused_and_the_cart_stays_as_it_was()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 3);
        Guid hat = _catalog.Product("Raffia hat", 3000m, available: false);
        await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2)), default);

        var soldOut = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.AddToCartAsync(Ws, Change("key-2", More(hat)), default));
        var tooMany = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.AddToCartAsync(Ws, Change("key-3", More(bag, 2)), default));

        Assert.Contains("sold out", soldOut.Message);
        Assert.Contains("Only 3 Quilted bag left", tooMany.Message);
        Assert.Equal((1, 2), (Assert.Single(_orders.Orders).Lines.Count, _orders.Orders[0].Lines[0].Quantity));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task Adding_needs_a_quantity_in_range(int? quantity)
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.AddToCartAsync(Ws, Change("key-1", new CartItemChange(bag, null, quantity)), default));
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.AddToCartAsync(Ws, Change("key-1"), default));
        Assert.Empty(_orders.Orders);
    }

    [Fact]
    public async Task While_the_customer_is_paying_nothing_is_added_or_taken_out_and_no_new_cart_is_made()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);
        await _service.PayAsync(TokenOf(first), new PayCheckoutRequest(4000m), default);

        var add = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.AddToCartAsync(Ws, Change("key-2", More(bag)), default));
        var remove = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.RemoveFromCartAsync(Ws, Change("key-3", Less(bag)), default));

        Assert.Contains("already pressed Pay", add.Message);
        Assert.Contains("already pressed Pay", remove.Message);
        Assert.Equal(PaymentOrderStatus.Pending, Assert.Single(_orders.Orders).Status);
    }

    [Fact]
    public async Task With_no_open_cart_there_is_nothing_to_take_out_but_adding_starts_a_new_one()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);
        _clock.Advance(TimeSpan.FromMinutes(31));

        var remove = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.RemoveFromCartAsync(Ws, Change("key-2", Less(bag)), default));
        CheckoutResult added = await _service.AddToCartAsync(Ws, Change("key-3", More(bag, 2)), default);

        Assert.Contains("no open cart", remove.Message);
        Assert.Equal(CheckoutService.CartReady, added.Status);
        Assert.NotEqual(first.OrderId, added.OrderId);
        Assert.Equal(2, added.Lines.Single().Quantity);
    }

    [Fact]
    public async Task A_change_that_lands_at_the_same_time_isnt_lost()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);

        // Another change saves between this one reading the cart and saving it: this one reads again and adds to that.
        _orders.BeforeConditionalUpdate = stored =>
        {
            stored.Lines = [stored.Lines[0] with { Quantity = 5, LineTotalMinor = 2_000_000 }];
            stored.UpdatedAt = stored.UpdatedAt.AddSeconds(1);
        };
        CheckoutResult added = await _service.AddToCartAsync(Ws, Change("key-2", More(bag)), default);

        Assert.Equal((6, 24000m), (added.Lines.Single().Quantity, added.Total));
    }

    [Fact]
    public async Task Held_at_checkout_adding_moves_the_kept_stock_to_the_whole_cart()
    {
        _rules.Set(Ws, hold: CheckoutHoldMode.AtCheckout);
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        Guid purse = _catalog.Product("Beaded purse", 2500m, left: 5);
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2)), default);
        string before = _orders.Get(first.OrderId).Reference;

        await _service.AddToCartAsync(Ws, Change("key-2", More(purse), More(bag)), default);

        PaymentOrder order = _orders.Get(first.OrderId);
        Assert.Contains(before, _stock.Released);
        var moved = _stock.Held.Last();
        Assert.Equal(order.Reference, moved.Reference);
        Assert.Equal([(bag, 3), (purse, 1)], moved.Lines.Select(l => (l.ProductId, l.Quantity)));
    }

    [Fact]
    public async Task Another_workspaces_agent_or_checkouts_off_changes_nothing()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);

        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.AddToCartAsync(Ws, Change("key-2", More(bag)) with { AgentId = _otherAgent }, default));
        _flags.On = false;
        await Assert.ThrowsAsync<CheckoutUnavailableException>(() => _service.RemoveFromCartAsync(Ws, Change("key-3", Less(bag)), default));

        Assert.Equal(1, Assert.Single(_orders.Orders).Lines.Single().Quantity);
    }

    // ---- get_cart --------------------------------------------------------------------

    [Fact]
    public async Task The_conversations_cart_is_found_with_its_items_ids_and_link()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        (Guid basket, Guid[] sizes) = _catalog.WithOptions("Woven basket", ("Small", 9000m, true, 3), ("Large", 15000m, true, null));
        CheckoutResult made = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2), Item(basket, 1, sizes[1])), default);

        CartStatus cart = await _service.GetCartAsync(Ws, null, _whatsApp, Sender, default);

        Assert.Equal((made.OrderId, CheckoutState.Open, 23000m, true, (string?)null), (cart.OrderId, cart.State, cart.Total, cart.CanPay, cart.Problem));
        Assert.Collection(cart.Items,
            line => Assert.Equal((bag, (Guid?)null, "Quilted bag", 2, 4000m), (line.ProductId, line.VariantId, line.Name, line.Quantity, line.UnitPrice!.Value)),
            line => Assert.Equal((basket, (Guid?)sizes[1], "Large", 1, 15000m), (line.ProductId, line.VariantId, line.VariantName!, line.Quantity, line.UnitPrice!.Value)));
        string url = Assert.IsType<string>(cart.CheckoutUrl);
        Assert.Equal(_links.Read(TokenOf(made)), _links.Read(url[(url.LastIndexOf('/') + 1)..]));
        Assert.Contains("create_cart with the complete new list", cart.Guidance);
    }

    [Fact]
    public async Task A_cart_is_priced_again_and_shows_what_moved_and_what_cant_be_had()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        Guid hat = _catalog.Product("Raffia hat", 3000m);
        CheckoutResult made = await _service.CreateAsync(Ws, Request("key-1", Item(bag), Item(hat)), default);

        _catalog.Change(bag, price: 4500m);
        CartStatus repriced = await _service.GetCartAsync(Ws, made.OrderId.ToString(), null, null, default);
        Assert.Equal((true, true, 7500m, 7000m), (repriced.PriceChanged, repriced.CanPay, repriced.Total, repriced.QuotedTotal));
        Assert.Equal((4000m, 4500m, true), (repriced.Items[0].QuotedUnitPrice, repriced.Items[0].UnitPrice!.Value, repriced.Items[0].PriceChanged));
        Assert.Contains("new total", repriced.Guidance);

        _catalog.Change(hat, available: false);
        CartStatus blocked = await _service.GetCartAsync(Ws, made.OrderId.ToString(), null, null, default);
        Assert.False(blocked.CanPay);
        Assert.Equal("Raffia hat is sold out.", blocked.Items[1].Problem);
        Assert.NotNull(blocked.Problem);
        Assert.Contains("can't pay this cart", blocked.Guidance);
    }

    [Fact]
    public async Task A_cart_being_paid_paid_for_replaced_or_run_out_says_so()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);
        await _service.PayAsync(TokenOf(first), new PayCheckoutRequest(4000m), default);

        CartStatus paying = await _service.GetCartAsync(Ws, first.OrderId.ToString(), null, null, default);
        Assert.Equal((CheckoutState.Paying, false), (paying.State, paying.CanPay));
        Assert.NotNull(paying.CheckoutUrl);

        CheckoutResult second = await _service.CreateAsync(Ws, Request("key-2", Item(bag, 2)), default);
        CartStatus replaced = await _service.GetCartAsync(Ws, first.OrderId.ToString(), null, null, default);
        Assert.Equal((CheckoutState.Replaced, (string?)null), (replaced.State, replaced.CheckoutUrl));
        Assert.Equal(second.OrderId, (await _service.GetCartAsync(Ws, null, _whatsApp, Sender, default)).OrderId);

        _clock.Advance(TimeSpan.FromMinutes(31));
        CartStatus lapsed = await _service.GetCartAsync(Ws, null, _whatsApp, Sender, default);
        Assert.Equal((CheckoutState.Expired, (string?)null, 1), (lapsed.State, lapsed.CheckoutUrl, lapsed.Items.Count));
    }

    [Fact]
    public async Task A_cart_is_found_by_its_id_in_quotes_or_its_reference_but_only_in_its_own_workspace()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult made = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);
        string reference = _orders.Get(made.OrderId).Reference;

        Assert.Equal(made.OrderId, (await _service.GetCartAsync(Ws, $"\"{made.OrderId}\"", null, null, default)).OrderId);
        Assert.Equal(made.OrderId, (await _service.GetCartAsync(Ws, reference, null, null, default)).OrderId);
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.GetCartAsync("ws-2", made.OrderId.ToString(), null, null, default));
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.GetCartAsync("ws-2", reference, null, null, default));
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.GetCartAsync(Ws, "not an id", null, null, default));
    }

    [Fact]
    public async Task Without_a_cart_or_a_way_to_find_one_the_agent_is_told_what_to_do()
    {
        var none = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.GetCartAsync(Ws, null, _whatsApp, Sender, default));
        Assert.Contains("no cart yet", none.Message);
        var nothing = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.GetCartAsync(Ws, null, null, null, default));
        Assert.Contains("Pass order_id, or connection_id and sender", nothing.Message);
    }

    // ---- The customer changing their cart on the page -----------------------------------

    private static ChangeCheckoutRequest Keep(params CheckoutItem[] items) => new(items);

    [Fact]
    public async Task The_customer_can_lower_a_quantity_and_take_an_item_out()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        Guid purse = _catalog.Product("Beaded purse", 2500m);
        CheckoutResult made = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 3), Item(purse)), default);
        DateTime? expires = _orders.Get(made.OrderId).ExpiresAt;
        _clock.Advance(TimeSpan.FromMinutes(5));

        CheckoutView view = await _service.ChangeAsync(TokenOf(made), Keep(Item(bag, 1), Item(purse, 0)), default);

        CheckoutLineView line = Assert.Single(view.Lines);
        Assert.Equal(("Quilted bag", 1, bag, 4000m), (line.Name, line.Quantity, line.ProductId!.Value, view.Total));
        Assert.True(view.CanPay);
        PaymentOrder order = Assert.Single(_orders.Orders);
        Assert.Equal((400_000L, 400_000L, expires), (order.TotalMinor, order.QuotedTotalMinor, order.ExpiresAt));
        Assert.Equal(PaymentOrderStatus.AwaitingCheckout, order.Status);
    }

    [Fact]
    public async Task The_customer_can_raise_a_quantity_up_to_what_is_left_and_no_further()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 3);
        CheckoutResult made = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);
        Assert.Equal(3, (await _service.GetAsync(TokenOf(made), default)).Lines[0].MaxQuantity);

        CheckoutView raised = await _service.ChangeAsync(TokenOf(made), Keep(Item(bag, 3)), default);
        Assert.Equal((3, 12000m), (raised.Lines[0].Quantity, raised.Total));

        var refused = await Assert.ThrowsAsync<CheckoutChangedException>(() => _service.ChangeAsync(TokenOf(made), Keep(Item(bag, 4)), default));
        Assert.Equal("Only 3 Quilted bag left. Your order hasn't changed.", refused.Message);
        Assert.Equal(3, refused.View.Lines[0].Quantity);
        Assert.Equal(3, _orders.Get(made.OrderId).Lines.Single().Quantity);
    }

    [Fact]
    public async Task Uncounted_stock_lets_the_customer_have_up_to_the_checkouts_limit()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult made = await _service.CreateAsync(Ws, Request("key-1", Item(bag)), default);

        Assert.Equal(CheckoutService.MaxQuantity, (await _service.GetAsync(TokenOf(made), default)).Lines[0].MaxQuantity);
    }

    [Fact]
    public async Task The_customer_cant_add_an_item_take_everything_out_or_ask_for_less_than_none()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        Guid hat = _catalog.Product("Raffia hat", 3000m);
        CheckoutResult made = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2)), default);

        var added = await Assert.ThrowsAsync<CheckoutChangedException>(() => _service.ChangeAsync(TokenOf(made), Keep(Item(bag, 2), Item(hat)), default));
        Assert.Contains("Only items already in your order", added.Message);
        var emptied = await Assert.ThrowsAsync<CheckoutChangedException>(() => _service.ChangeAsync(TokenOf(made), Keep(Item(bag, 0)), default));
        Assert.Contains("Keep at least one item", emptied.Message);
        var negative = await Assert.ThrowsAsync<CheckoutChangedException>(() => _service.ChangeAsync(TokenOf(made), Keep(Item(bag, -1)), default));
        Assert.Contains("Choose between 1 and", negative.Message);

        Assert.Equal(2, _orders.Get(made.OrderId).Lines.Single().Quantity);
    }

    [Fact]
    public async Task A_price_that_moved_still_shows_after_the_customer_changes_the_quantity()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult made = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2)), default);
        _catalog.Change(bag, price: 4500m);

        CheckoutView view = await _service.ChangeAsync(TokenOf(made), Keep(Item(bag, 3)), default);

        Assert.Equal((true, 13500m, 12000m), (view.PriceChanged, view.Total, view.QuotedTotal));
        Assert.Equal((4000m, 4500m), (view.Lines[0].QuotedUnitPrice, view.Lines[0].UnitPrice!.Value));
    }

    [Fact]
    public async Task Once_pay_is_pressed_the_customer_cant_change_the_cart()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult made = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2)), default);
        await _service.PayAsync(TokenOf(made), new PayCheckoutRequest(8000m), default);

        var refused = await Assert.ThrowsAsync<CheckoutChangedException>(() => _service.ChangeAsync(TokenOf(made), Keep(Item(bag, 1)), default));

        Assert.Contains("already started paying", refused.Message);
        Assert.Equal(CheckoutState.Paying, refused.View.State);
        Assert.Equal(2, _orders.Get(made.OrderId).Lines.Single().Quantity);
    }

    [Fact]
    public async Task Held_at_checkout_the_customer_can_have_what_is_left_plus_their_own_and_the_hold_follows()
    {
        _rules.Set(Ws, hold: CheckoutHoldMode.AtCheckout);
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 3);
        CheckoutResult made = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2)), default);
        _catalog.Change(bag, left: 1);
        Assert.Equal(3, (await _service.GetAsync(TokenOf(made), default)).Lines[0].MaxQuantity);

        CheckoutView view = await _service.ChangeAsync(TokenOf(made), Keep(Item(bag, 1)), default);

        PaymentOrder order = _orders.Get(made.OrderId);
        Assert.Equal(1, view.Lines[0].Quantity);
        var moved = _stock.Held.Last();
        Assert.Equal((order.Reference, 1), (moved.Reference, moved.Lines.Single().Quantity));
    }

    [Fact]
    public async Task Held_at_checkout_a_change_moves_the_kept_stock_to_the_new_items()
    {
        _rules.Set(Ws, hold: CheckoutHoldMode.AtCheckout);
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 3);
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2)), default);
        string before = _orders.Get(first.OrderId).Reference;

        // Its own two count as free: three can be had.
        _catalog.Change(bag, left: 1);
        CheckoutResult changed = await _service.CreateAsync(Ws, Request("key-2", Item(bag, 3)), default);

        PaymentOrder order = _orders.Get(changed.OrderId);
        Assert.Equal(first.OrderId, changed.OrderId);
        Assert.Contains(before, _stock.Released);
        Assert.NotEqual(before, order.Reference);
        var moved = _stock.Held.Last();
        Assert.Equal((order.Reference, 3), (moved.Reference, moved.Lines.Single().Quantity));
        Assert.True(moved.ExpiresAt > order.ExpiresAt);
    }

    [Fact]
    public async Task Held_at_checkout_a_change_the_stock_cant_cover_leaves_the_cart_and_its_stock_as_they_were()
    {
        _rules.Set(Ws, hold: CheckoutHoldMode.AtCheckout);
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 3);
        CheckoutResult first = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2)), default);
        _stock.ShortIf = lines => lines.Sum(l => l.Quantity) > 2 ? "Only 2 Quilted bag left." : null;

        var refused = await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("key-2", Item(bag, 5)), default));

        Assert.Contains("Only 2 Quilted bag left.", refused.Message);
        Assert.Contains("wasn't changed", refused.Message);
        PaymentOrder order = Assert.Single(_orders.Orders);
        Assert.Equal((first.OrderId, 2, 800_000L, CheckoutHoldMode.AtCheckout), (order.Id, order.Lines.Single().Quantity, order.TotalMinor, order.HoldMode));
        var again = _stock.Held.Last();
        Assert.Equal((order.Reference, 2), (again.Reference, again.Lines.Single().Quantity));
    }

    [Fact]
    public async Task Held_at_checkout_if_even_the_old_stock_cant_be_kept_again_pay_checks_it_instead()
    {
        _rules.Set(Ws, hold: CheckoutHoldMode.AtCheckout);
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 3);
        await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2)), default);
        _stock.ShortWith = "Only 1 Quilted bag left.";

        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("key-2", Item(bag, 3)), default));

        PaymentOrder order = Assert.Single(_orders.Orders);
        Assert.Equal((2, CheckoutHoldMode.AtPay), (order.Lines.Single().Quantity, order.HoldMode));
        Assert.DoesNotContain(_stock.Held, h => h.Reference == order.Reference);
    }

    [Fact]
    public async Task The_same_item_asked_for_twice_is_one_line()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);

        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("key-1", Item(bag, 2), Item(bag, 1)), default);

        Assert.Equal(3, Assert.Single(checkout.Lines).Quantity);
        Assert.Equal(12000m, checkout.Total);
    }

    [Fact]
    public async Task Every_problem_with_the_items_is_reported_at_once_and_nothing_is_made()
    {
        Guid soldOut = _catalog.Product("Beaded purse", 3000m, left: 0, available: false);
        Guid few = _catalog.Product("Quilted bag", 4000m, left: 2);
        Guid quote = _catalog.Product("Custom clutch", null);
        Guid unknown = Guid.NewGuid();

        var refused = await Assert.ThrowsAsync<CheckoutRefusedException>(() =>
            _service.CreateAsync(Ws, Request("key-1", Item(soldOut), Item(few, 3), Item(quote), Item(unknown)), default));

        Assert.Contains($"{unknown} isn't on sale.", refused.Message);
        Assert.Empty(_orders.Orders);

        refused = await Assert.ThrowsAsync<CheckoutRefusedException>(() =>
            _service.CreateAsync(Ws, Request("key-1", Item(soldOut), Item(few, 3), Item(quote)), default));
        Assert.Contains("Beaded purse is sold out.", refused.Message);
        Assert.Contains("Only 2 Quilted bag left.", refused.Message);
        Assert.Contains("Custom clutch is priced on request", refused.Message);
        Assert.Empty(_orders.Orders);
    }

    [Fact]
    public async Task An_option_on_a_product_without_options_or_a_wrong_option_is_refused()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        (Guid basket, _) = _catalog.WithOptions("Woven basket", ("Small", 9000m, true, null));

        Assert.Contains("has no options", (await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("k", Item(bag, 1, Guid.NewGuid())), default))).Message);
        Assert.Contains("comes in options (Small)", (await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("k", Item(basket)), default))).Message);
        Assert.Contains("isn't one of Woven basket's options", (await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("k", Item(basket, 1, Guid.NewGuid())), default))).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000)]
    public async Task A_quantity_out_of_range_is_refused(int quantity)
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("k", Item(bag, quantity)), default));
    }

    [Fact]
    public async Task No_items_too_many_items_or_no_key_is_refused()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("k"), default));
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("k", Enumerable.Range(0, 21).Select(_ => Item(Guid.NewGuid())).ToArray()), default));
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request(" ", Item(bag)), default));
    }

    [Fact]
    public async Task Another_workspaces_agent_or_conversation_is_refused()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        Guid theirConnection = _connections.Add("ws-2", MessagingPlatform.WhatsApp);

        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("k", Item(bag)) with { AgentId = _otherAgent }, default));
        await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("k", Item(bag)) with { ConnectionId = theirConnection }, default));
        Assert.Empty(_orders.Orders);
    }

    [Fact]
    public async Task A_total_over_the_limit_or_another_currency_is_refused()
    {
        Guid dear = _catalog.Product("Gold bag", 60_000m);
        Guid dollars = _catalog.Product("Imported bag", 50m, currency: "USD");

        Assert.Contains("over the", (await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("k", Item(dear, 2)), default))).Message);
        Assert.Contains("only be made in NGN", (await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("k", Item(dollars)), default))).Message);
    }

    [Fact]
    public async Task Checkouts_off_makes_nothing()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        _flags.On = false;

        await Assert.ThrowsAsync<CheckoutUnavailableException>(() => _service.CreateAsync(Ws, Request("k", Item(bag)), default));
        Assert.Empty(_orders.Orders);
    }

    [Fact]
    public async Task A_checkout_is_made_without_the_payment_provider_which_is_only_needed_to_pay()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        _provider.Configured = false;

        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag)), default);
        Assert.True((await _service.GetAsync(TokenOf(checkout), default)).CanPay);

        await Assert.ThrowsAsync<CheckoutUnavailableException>(() => _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(4000m), default));
        Assert.Equal(PaymentOrderStatus.AwaitingCheckout, _orders.Get(checkout.OrderId).Status);
        Assert.Empty(_stock.Held);
        Assert.Empty(_provider.Calls);
    }

    [Fact]
    public async Task A_catalog_outage_is_unavailable_and_a_catalog_refusal_is_passed_on()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        _catalog.FailWith = new CatalogToolException("The catalog is down.", refused: false);
        await Assert.ThrowsAsync<CheckoutUnavailableException>(() => _service.CreateAsync(Ws, Request("k", Item(bag)), default));

        _catalog.FailWith = new CatalogToolException("ids may hold at most 20 products.", refused: true);
        Assert.Contains("at most 20", (await Assert.ThrowsAsync<CheckoutRefusedException>(() => _service.CreateAsync(Ws, Request("k", Item(bag)), default))).Message);
    }

    [Fact]
    public async Task The_workspaces_checkout_minutes_set_when_the_link_expires()
    {
        _rules.Set(Ws, checkoutMinutes: 120);
        Guid bag = _catalog.Product("Quilted bag", 4000m);

        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag)), default);

        Assert.Equal(_clock.Now.UtcDateTime.AddMinutes(120), checkout.ExpiresAt);
    }

    // ---- Holding stock at checkout ----------------------------------------------------

    [Fact]
    public async Task Held_at_checkout_the_stock_is_kept_from_the_start_and_a_short_line_makes_nothing_payable()
    {
        _rules.Set(Ws, hold: CheckoutHoldMode.AtCheckout);
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);

        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag, 2)), default);

        var held = Assert.Single(_stock.Held);
        Assert.Equal((Ws, _orders.Get(checkout.OrderId).Reference), (held.Workspace, held.Reference));
        Assert.True(held.ExpiresAt > checkout.ExpiresAt);
        Assert.Equal(CheckoutHoldMode.AtCheckout, _orders.Get(checkout.OrderId).HoldMode);

        _stock.ShortWith = "Only 1 Quilted bag left.";
        // Another customer's cart: a new one, which can't be made without its stock.
        var refused = await Assert.ThrowsAsync<CheckoutRefusedException>(() =>
            _service.CreateAsync(Ws, new CreateCheckoutRequest(_agent, _whatsApp, "2348099999999", [Item(bag, 2)], "k2"), default));
        Assert.Contains("Only 1 Quilted bag left.", refused.Message);
        Assert.Equal(PaymentOrderStatus.Failed, _orders.Orders.Single(o => o.IdempotencyKey == "k2").Status);
    }

    [Fact]
    public async Task Held_at_checkout_pressing_pay_doesnt_hold_again_or_count_its_own_stock()
    {
        _rules.Set(Ws, hold: CheckoutHoldMode.AtCheckout);
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 2);
        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag, 2)), default);

        // The catalog now counts this order's own hold: none left for anyone else.
        _catalog.Change(bag, left: 0);
        CheckoutView view = await _service.GetAsync(TokenOf(checkout), default);
        Assert.True(view.CanPay);

        await _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(8000m), default);

        Assert.Single(_stock.Held);
        Assert.Equal(PaymentOrderStatus.Pending, _orders.Get(checkout.OrderId).Status);
    }

    // ---- The customer's page and Pay -----------------------------------------------------

    [Fact]
    public async Task Pressing_pay_holds_the_stock_for_the_payment_and_makes_the_link()
    {
        _rules.Set(Ws, paymentMinutes: 20);
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag, 2)), default);

        CheckoutView open = await _service.GetAsync(TokenOf(checkout), default);
        Assert.Equal((CheckoutState.Open, "Ada's Bags", "Kemi", 8000m, true, false), (open.State, open.Business, open.CustomerName, open.Total, open.CanPay, open.PriceChanged));

        _clock.Advance(TimeSpan.FromMinutes(10));
        PayCheckoutResult paying = await _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(8000m), default);

        PaymentOrder order = _orders.Get(checkout.OrderId);
        Assert.Equal(PaymentOrderStatus.Pending, order.Status);
        Assert.Equal(order.PaymentUrl, paying.PaymentUrl);
        Assert.Equal(_clock.Now.UtcDateTime.AddMinutes(20), order.ExpiresAt);
        Assert.Equal(_clock.Now.UtcDateTime, order.PaymentStartedAt);
        var held = Assert.Single(_stock.Held);
        Assert.Equal((order.Reference, order.ExpiresAt!.Value), (held.Reference, held.ExpiresAt));
        Assert.Equal((order.Reference, 800_000L, "Ada's Bags: 2 x Quilted bag"), Assert.Single(_provider.Calls));

        // After paying, the customer comes back to this checkout, which waits for the payment to be confirmed.
        Assert.Equal(_links.For(order) + "?paid=1", Assert.Single(_provider.ReturnUrls));

        // Pressing Pay again, or opening the page, gives the same link.
        Assert.Equal(paying.PaymentUrl, (await _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(8000m), default)).PaymentUrl);
        CheckoutView now = await _service.GetAsync(TokenOf(checkout), default);
        Assert.Equal((CheckoutState.Paying, paying.PaymentUrl), (now.State, now.PaymentUrl));
        Assert.Single(_provider.Calls);
    }

    [Fact]
    public async Task A_price_change_is_shown_old_and_new_and_charged_only_once_the_customer_agrees()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag, 2)), default);
        _catalog.Change(bag, price: 4500m);

        CheckoutView view = await _service.GetAsync(TokenOf(checkout), default);
        Assert.True(view.PriceChanged);
        Assert.True(view.CanPay);
        Assert.Equal((8000m, 9000m), (view.QuotedTotal, view.Total));
        CheckoutLineView line = Assert.Single(view.Lines);
        Assert.Equal((4000m, (decimal?)4500m, true), (line.QuotedUnitPrice, line.UnitPrice, line.PriceChanged));

        // Paying the old total is refused with the checkout as it is now.
        var changed = await Assert.ThrowsAsync<CheckoutChangedException>(() => _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(8000m), default));
        Assert.Contains("9,000.00", changed.Message);
        Assert.Equal(9000m, changed.View.Total);
        Assert.Empty(_provider.Calls);

        await _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(9000m), default);

        PaymentOrder order = _orders.Get(checkout.OrderId);
        Assert.Equal((900_000L, 800_000L), (order.TotalMinor, order.QuotedTotalMinor));
        PaymentOrderLine paid = Assert.Single(order.Lines);
        Assert.Equal((450_000L, 400_000L), (paid.UnitPriceMinor, paid.QuotedUnitMinor));
        Assert.Equal(900_000L, Assert.Single(_provider.Calls).AmountMinor);
    }

    [Fact]
    public async Task When_the_business_blocks_price_changes_the_checkout_cant_be_paid()
    {
        _rules.Set(Ws, rule: PriceChangeRule.Block);
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag)), default);
        _catalog.Change(bag, price: 3500m);

        CheckoutView view = await _service.GetAsync(TokenOf(checkout), default);
        Assert.False(view.CanPay);
        Assert.Contains("new one", view.Message);

        await Assert.ThrowsAsync<CheckoutChangedException>(() => _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(3500m), default));
        Assert.Empty(_provider.Calls);
    }

    [Fact]
    public async Task An_item_that_sold_out_since_is_shown_and_nothing_is_charged_or_held()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag, 2)), default);
        _catalog.Change(bag, left: 1);

        CheckoutView view = await _service.GetAsync(TokenOf(checkout), default);
        Assert.False(view.CanPay);
        Assert.Equal("Only 1 Quilted bag left.", Assert.Single(view.Lines).Problem);

        await Assert.ThrowsAsync<CheckoutChangedException>(() => _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(8000m), default));
        Assert.Empty(_stock.Held);
        Assert.Empty(_provider.Calls);
        Assert.Equal(PaymentOrderStatus.AwaitingCheckout, _orders.Get(checkout.OrderId).Status);
    }

    [Fact]
    public async Task Stock_taken_as_pay_is_pressed_opens_the_checkout_again()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag, 2)), default);
        _stock.ShortWith = "Only 1 Quilted bag left.";

        var changed = await Assert.ThrowsAsync<CheckoutChangedException>(() => _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(8000m), default));

        Assert.Contains("Only 1 Quilted bag left.", changed.Message);
        Assert.Equal(PaymentOrderStatus.AwaitingCheckout, _orders.Get(checkout.OrderId).Status);
        Assert.Empty(_provider.Calls);
    }

    [Fact]
    public async Task Never_holding_checks_stock_but_keeps_nothing_aside()
    {
        _rules.Set(Ws, hold: CheckoutHoldMode.Never);
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag, 2)), default);

        await _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(8000m), default);

        Assert.Empty(_stock.Held);
        Assert.Equal(PaymentOrderStatus.Pending, _orders.Get(checkout.OrderId).Status);
    }

    [Fact]
    public async Task An_expired_checkout_cant_be_paid()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag)), default);
        _clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(CheckoutState.Expired, (await _service.GetAsync(TokenOf(checkout), default)).State);
        var refused = await Assert.ThrowsAsync<CheckoutChangedException>(() => _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(4000m), default));
        Assert.Contains("expired", refused.Message);
        Assert.Empty(_provider.Calls);
    }

    [Fact]
    public async Task When_the_provider_fails_the_checkout_ends_and_the_stock_goes_back()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag)), default);
        _provider.FailWith = new PaymentProviderException("down", "500");

        await Assert.ThrowsAsync<CheckoutUnavailableException>(() => _service.PayAsync(TokenOf(checkout), new PayCheckoutRequest(4000m), default));

        PaymentOrder order = _orders.Get(checkout.OrderId);
        Assert.Equal(PaymentOrderStatus.Failed, order.Status);
        Assert.Equal([order.Reference], _stock.Released);
        Assert.Equal(CheckoutState.Unavailable, (await _service.GetAsync(TokenOf(checkout), default)).State);
    }

    [Fact]
    public async Task A_link_that_isnt_ours_or_names_another_workspace_finds_nothing()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m);
        CheckoutResult checkout = await _service.CreateAsync(Ws, Request("k", Item(bag)), default);
        PaymentOrder order = _orders.Get(checkout.OrderId);

        // Signed with another key, or naming this order under another workspace.
        CheckoutLinks forged = Links(_settings, "another-secret-that-is-long-enough-for-hmac!!");
        string foreign = new CheckoutTokens(Options.Create(new JwtSettings { Secret = "a-secret-long-enough-for-hmac-sha256-signing!!", Issuer = "parrot", Audience = "parrot-api" }))
            .Issue(order.Id, "ws-2", order.CheckoutExpiresAt!.Value);

        await Assert.ThrowsAsync<CheckoutNotFoundException>(() => _service.GetAsync(forged.For(order)[(forged.For(order).LastIndexOf('/') + 1)..], default));
        await Assert.ThrowsAsync<CheckoutNotFoundException>(() => _service.GetAsync(foreign, default));
        await Assert.ThrowsAsync<CheckoutNotFoundException>(() => _service.PayAsync("not-a-token", new PayCheckoutRequest(4000m), default));
    }
}

// Each workspace's own checkout settings: the platform's defaults until saved, checked on the way in.
public sealed class WorkspacePaymentSettingsServiceTests
{
    private readonly FakeWorkspacePaymentSettingsRepository _rows = new();
    private readonly WorkspacePaymentSettingsService _service;

    public WorkspacePaymentSettingsServiceTests()
    {
        _service = new WorkspacePaymentSettingsService(_rows, Options.Create(new PaymentSettings { CheckoutMinutes = 30, LinkMinutes = 15 }), new TestClock());
    }

    [Fact]
    public async Task A_workspace_that_never_saved_gets_the_defaults()
    {
        PaymentSettingsResponse settings = await _service.GetAsync("ws-1", default);

        Assert.Equal((30, CheckoutHoldMode.AtPay, 15, PriceChangeRule.ConfirmNewPrice, true), (settings.CheckoutMinutes, settings.HoldMode, settings.PaymentMinutes, settings.PriceChangeRule, settings.IsDefault));
    }

    [Fact]
    public async Task Saved_settings_belong_to_their_workspace_only()
    {
        await _service.UpdateAsync("ws-1", "user-1", new UpdatePaymentSettingsRequest(60, CheckoutHoldMode.AtCheckout, 30, PriceChangeRule.Block), default);

        PaymentSettingsResponse mine = await _service.GetAsync("ws-1", default);
        Assert.Equal((60, CheckoutHoldMode.AtCheckout, 30, PriceChangeRule.Block, false), (mine.CheckoutMinutes, mine.HoldMode, mine.PaymentMinutes, mine.PriceChangeRule, mine.IsDefault));
        Assert.Equal("user-1", _rows.Rows["ws-1"].UpdatedBy);
        Assert.True((await _service.GetAsync("ws-2", default)).IsDefault);
    }

    [Theory]
    [InlineData(4, 15)]
    [InlineData(1441, 15)]
    [InlineData(30, 4)]
    [InlineData(30, 121)]
    public async Task Minutes_out_of_range_are_refused(int checkoutMinutes, int paymentMinutes)
    {
        await Assert.ThrowsAsync<global::Parrot.Domain.Exceptions.ValidationException>(() =>
            _service.UpdateAsync("ws-1", null, new UpdatePaymentSettingsRequest(checkoutMinutes, CheckoutHoldMode.AtPay, paymentMinutes, PriceChangeRule.ConfirmNewPrice), default));
        Assert.Empty(_rows.Rows);
    }
}
