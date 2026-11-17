using System.Net;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Test.Parrot.Catalog.Fixtures;

namespace Test.Parrot.Catalog.Integration;

/// <summary>
/// Stock held for payment orders: what's held stops being offered, a hold nobody pays for
/// frees its stock when its time is up, a paid one comes off stock for good, and two orders
/// can never both get the last one.
/// </summary>
[Collection(CatalogApiCollection.Name)]
public class StockHoldTests
{
    private static readonly string[] Owner = [CatalogScopes.Read, CatalogScopes.Write, CatalogScopes.Publish];

    private readonly CatalogApiFixture _fx;

    public StockHoldTests(CatalogApiFixture fx)
    {
        _fx = fx;
    }

    [PostgresFact]
    public async Task Held_stock_is_no_longer_offered_and_a_paid_hold_comes_off_stock()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);
        using HttpClient ai = _fx.Client(workspace, CatalogScopes.AiTools);
        using HttpClient orders = _fx.Client(workspace, CatalogScopes.Orders);
        Guid bag = await AddAsync(owner, "Quilted bag", new { mode = "in_stock", quantity = 5 });
        Guid tote = await AddAsync(owner, "Leather tote", new { mode = "in_stock" });

        StockHoldsResponse held = await PlaceAsync(orders, "prt_a", Lines((bag, 3), (tote, 1)));
        Assert.Equal([true, false], held.Lines.Select(l => l.Held));
        Assert.Equal(2, await LeftAsync(ai, bag));

        // The same request again holds nothing more.
        await PlaceAsync(orders, "prt_a", Lines((bag, 3), (tote, 1)));
        Assert.Equal(2, await LeftAsync(ai, bag));

        // More than is left: refused with the name and how many there are, and nothing held.
        HttpResponseMessage tooMany = await orders.PostAsync("/api/catalog/orders/holds", Hold("prt_b", Lines((bag, 3))));
        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);
        Assert.Contains("Only 2 Quilted bag left.", await tooMany.Content.ReadAsStringAsync());
        Assert.Equal(2, await LeftAsync(ai, bag));

        CommitStockHoldsResponse paid = await PostAsync<CommitStockHoldsResponse>(orders, "/api/catalog/orders/holds/prt_a/commit");
        Assert.Empty(paid.Short);
        Assert.Equal(2, await LeftAsync(ai, bag));
        ItemResponse item = await CatalogApiFixture.ReadAsync<ItemResponse>(await owner.GetAsync($"/api/catalog/items/{bag}"));
        Assert.Equal(2, item.Availability!.Quantity);

        // Committing twice takes nothing more off.
        await PostAsync<CommitStockHoldsResponse>(orders, "/api/catalog/orders/holds/prt_a/commit");
        Assert.Equal(2, await LeftAsync(ai, bag));
    }

    [PostgresFact]
    public async Task A_hold_nobody_pays_for_frees_its_stock_when_released_or_when_its_time_is_up()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);
        using HttpClient ai = _fx.Client(workspace, CatalogScopes.AiTools);
        using HttpClient orders = _fx.Client(workspace, CatalogScopes.Orders);
        Guid bag = await AddAsync(owner, "Quilted bag", new { mode = "in_stock", quantity = 1 });

        await PlaceAsync(orders, "prt_a", Lines((bag, 1)));
        Assert.Equal(0, await LeftAsync(ai, bag));
        ReleaseStockHoldsResponse released = await PostAsync<ReleaseStockHoldsResponse>(orders, "/api/catalog/orders/holds/prt_a/release");
        Assert.Equal(1, released.Released);
        Assert.Equal(1, await LeftAsync(ai, bag));

        // Nobody has to release a hold for its time to run out.
        await PlaceAsync(orders, "prt_b", Lines((bag, 1)), seconds: 2);
        Assert.Equal(0, await LeftAsync(ai, bag));
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(1, await LeftAsync(ai, bag));
    }

    [PostgresFact]
    public async Task A_payment_after_the_hold_ran_out_takes_stock_if_there_is_some_and_reports_short_if_not()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);
        using HttpClient orders = _fx.Client(workspace, CatalogScopes.Orders);
        Guid bag = await AddAsync(owner, "Quilted bag", new { mode = "in_stock", quantity = 1 });
        Guid purse = await AddAsync(owner, "Beaded purse", new { mode = "in_stock", quantity = 1 });

        await PlaceAsync(orders, "prt_late", Lines((bag, 1), (purse, 1)), seconds: 2);
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // While it was out of time, someone else held the last purse.
        await PlaceAsync(orders, "prt_other", Lines((purse, 1)));

        CommitStockHoldsResponse late = await PostAsync<CommitStockHoldsResponse>(orders, "/api/catalog/orders/holds/prt_late/commit");
        Assert.Equal(bag, Assert.Single(late.Committed).ProductId);
        ShortLine missing = Assert.Single(late.Short);
        Assert.Equal((purse, 1, 0), (missing.ProductId, missing.Quantity, missing.Left));
    }

    [PostgresFact]
    public async Task Two_orders_at_once_never_both_get_the_last_one()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);
        using HttpClient orders = _fx.Client(workspace, CatalogScopes.Orders);
        Guid bag = await AddAsync(owner, "Quilted bag", new { mode = "in_stock", quantity = 1 });

        HttpResponseMessage[] answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            orders.PostAsync("/api/catalog/orders/holds", Hold($"prt_{i}", Lines((bag, 1))))));

        Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, answers.Count(a => a.StatusCode == HttpStatusCode.Conflict));
    }

    [PostgresFact]
    public async Task Only_a_catalog_orders_token_opens_holds_and_bad_requests_are_refused()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);
        using HttpClient ai = _fx.Client(workspace, CatalogScopes.AiTools);
        using HttpClient orders = _fx.Client(workspace, CatalogScopes.Orders);
        Guid bag = await AddAsync(owner, "Quilted bag", new { mode = "in_stock", quantity = 5 });

        Assert.Equal(HttpStatusCode.Forbidden, (await ai.PostAsync("/api/catalog/orders/holds", Hold("prt_a", Lines((bag, 1))))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/api/catalog/orders/holds", Hold("prt_a", Lines((bag, 1))))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await orders.PostAsync("/api/catalog/orders/holds", Hold("prt_a", Lines((bag, 0))))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await orders.PostAsync("/api/catalog/orders/holds", Hold("prt_a", Lines()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await orders.PostAsync("/api/catalog/orders/holds", Hold("prt_a", Lines((bag, 1)), seconds: -5))).StatusCode);

        // Another workspace's holds are out of reach.
        using HttpClient theirs = _fx.Client(Guid.NewGuid(), CatalogScopes.Orders);
        await PlaceAsync(orders, "prt_a", Lines((bag, 2)));
        ReleaseStockHoldsResponse released = await PostAsync<ReleaseStockHoldsResponse>(theirs, "/api/catalog/orders/holds/prt_a/release");
        Assert.Equal(0, released.Released);
        Assert.Equal(3, await LeftAsync(ai, bag));
    }

    [PostgresFact]
    public async Task Options_without_their_own_count_share_the_products_stock()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);
        using HttpClient ai = _fx.Client(workspace, CatalogScopes.AiTools);
        using HttpClient orders = _fx.Client(workspace, CatalogScopes.Orders);
        HttpResponseMessage added = await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Body(new
        {
            name = "Zobo",
            kind = "food",
            pricing = new { mode = "variants", variants = new[] { new { name = "35cl", amount = 500 }, new { name = "50cl", amount = 700 } } },
            availability = new { mode = "in_stock", quantity = 5 },
            status = "published",
        }));
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        ItemResponse zobo = await CatalogApiFixture.ReadAsync<ItemResponse>(added);
        Guid small = zobo.Pricing.Variants.Single(v => v.Name == "35cl").Id;
        Guid large = zobo.Pricing.Variants.Single(v => v.Name == "50cl").Id;
        object Line(Guid variant, int quantity) => new { product_id = zobo.Id, variant_id = variant, quantity };

        Assert.True(Assert.Single((await PlaceAsync(orders, "prt_a", [Line(small, 3)])).Lines).Held);
        Assert.Equal(2, await LeftAsync(ai, zobo.Id));

        // The other option draws on the same five.
        HttpResponseMessage tooMany = await orders.PostAsync("/api/catalog/orders/holds", Hold("prt_b", [Line(large, 3)]));
        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);
        Assert.Contains("Only 2 Zobo (50cl) left.", await tooMany.Content.ReadAsStringAsync());
        await PlaceAsync(orders, "prt_c", [Line(large, 2)]);
        Assert.Equal(0, await LeftAsync(ai, zobo.Id));

        await PostAsync<CommitStockHoldsResponse>(orders, "/api/catalog/orders/holds/prt_a/commit");
        ItemResponse item = await CatalogApiFixture.ReadAsync<ItemResponse>(await owner.GetAsync($"/api/catalog/items/{zobo.Id}"));
        Assert.Equal(2, item.Availability!.Quantity);
        Assert.Equal(0, await LeftAsync(ai, zobo.Id));
    }

    private static object[] Lines(params (Guid Product, int Quantity)[] lines) =>
        lines.Select(l => (object)new { product_id = l.Product, quantity = l.Quantity }).ToArray();

    private static HttpContent Hold(string reference, object[] lines, int seconds = 600) =>
        CatalogApiFixture.Body(new { reference, expires_at = DateTime.UtcNow.AddSeconds(seconds), lines });

    private static async Task<StockHoldsResponse> PlaceAsync(HttpClient orders, string reference, object[] lines, int seconds = 600)
    {
        HttpResponseMessage response = await orders.PostAsync("/api/catalog/orders/holds", Hold(reference, lines, seconds));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await CatalogApiFixture.ReadAsync<StockHoldsResponse>(response);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await CatalogApiFixture.ReadAsync<T>(response);
    }

    /// <summary>What get_prices says is left.</summary>
    private static async Task<int?> LeftAsync(HttpClient ai, Guid product)
    {
        HttpResponseMessage response = await ai.PostAsync("/api/catalog/ai/tools/get_prices", CatalogApiFixture.Body(new { ids = new[] { product } }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AiToolResponse<AiPricesResult> answer = await CatalogApiFixture.ReadAsync<AiToolResponse<AiPricesResult>>(response);
        return Assert.Single(answer.Result.Items).QuantityLeft;
    }

    private static async Task<Guid> AddAsync(HttpClient owner, string name, object availability)
    {
        HttpResponseMessage response = await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Body(new
        {
            name,
            kind = "product",
            pricing = new { mode = "single", amount = 4000 },
            availability,
            status = "published",
        }));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await CatalogApiFixture.ReadAsync<ItemResponse>(response)).Id;
    }
}
