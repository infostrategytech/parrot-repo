using System.Net;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Domain.Enums;
using Test.Parrot.Catalog.Fixtures;

namespace Test.Parrot.Catalog.Integration;

/// <summary>
/// get_prices: the AI finds products in its own index, then asks the catalog what they
/// cost and whether they can be had now. Items are made through the dashboard's form,
/// so the answer is checked against what the business actually entered.
/// </summary>
[Collection(CatalogApiCollection.Name)]
public class PriceToolTests
{
    private static readonly string[] Owner = [CatalogScopes.Read, CatalogScopes.Write, CatalogScopes.Publish];

    private readonly CatalogApiFixture _fx;

    public PriceToolTests(CatalogApiFixture fx)
    {
        _fx = fx;
    }

    [PostgresFact]
    public async Task The_ai_gets_live_prices_and_quantity_left_and_can_keep_to_a_price_range()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);
        using HttpClient ai = _fx.Client(workspace, CatalogScopes.AiTools);

        Guid bag = await AddAsync(owner, "Quilted bag", new { mode = "single", amount = 4000 }, new { mode = "in_stock", quantity = 5 });
        Guid tote = await AddAsync(owner, "Leather tote", new { mode = "single", amount = 12000 }, new { mode = "in_stock" });
        Guid purse = await AddAsync(owner, "Beaded purse", new { mode = "single", amount = 3000 }, new { mode = "in_stock", quantity = 0 });
        Guid sizes = await AddAsync(owner, "Woven basket", new
        {
            mode = "variants",
            variants = new object[] { new { name = "Small", amount = 9000, available = false }, new { name = "Large", amount = 15000 } },
        }, new { mode = "in_stock" });
        Guid quote = await AddAsync(owner, "Custom clutch", new { mode = "on_request" }, new { mode = "made_to_order", lead_time_days = 7 });
        Guid hidden = await AddAsync(owner, "Old satchel", new { mode = "single", amount = 2000 }, new { mode = "in_stock" }, hide: true);
        Guid draft = await AddAsync(owner, "Unfinished bag", new { mode = "single", amount = 1000 }, new { mode = "in_stock" }, status: "draft");
        Guid unknown = Guid.NewGuid();

        Guid[] asked = [tote, purse, bag, sizes, quote, hidden, draft, unknown];

        // No range: every product on sale, available first, then cheapest first, with how many are left.
        AiPricesResult all = await PricesAsync(ai, new { ids = asked });
        Assert.Equal(new[] { hidden, draft, unknown }.Order(), all.NotOnSale.Order());
        Assert.Empty(all.Closest);
        Assert.Equal([bag, tote, sizes, quote, purse], all.Items.Select(i => i.ProductId));
        AiItemPrice first = all.Items[0];
        Assert.True(first.Available);
        Assert.Equal(4000m, first.Price);
        Assert.Equal("NGN", first.Currency);
        Assert.Equal(5, first.QuantityLeft);
        Assert.Equal(AvailabilityMode.InStock, first.Mode);
        Assert.Null(first.InRange);
        Assert.Null(all.Items.Single(i => i.ProductId == tote).QuantityLeft);
        Assert.Equal(0, all.Items.Single(i => i.ProductId == purse).QuantityLeft);

        // A range: only what fits, and the rest nearest first.
        AiPricesResult ranged = await PricesAsync(ai, new { ids = asked, min_price = 3500, max_price = 10000 });
        AiItemPrice fits = Assert.Single(ranged.Items);
        Assert.Equal(bag, fits.ProductId);
        Assert.True(fits.InRange);

        // 12,000 is 2,000 over; the sold-out purse at 3,000 is 500 under but can't be had; the basket's
        // available option is 15,000 (the 9,000 one is sold out); the quoted clutch comes last.
        Assert.Equal([tote, sizes, purse, quote], ranged.Closest.Select(i => i.ProductId));
        AiItemPrice basket = ranged.Closest.Single(i => i.ProductId == sizes);
        Assert.False(basket.InRange);
        Assert.Equal(15000m, basket.Price);
        AiVariantPrice small = basket.Variants.Single(v => v.Name.Value == "Small");
        Assert.False(small.Available);
        Assert.Equal(0, small.QuantityLeft);
        Assert.Equal(9000m, small.Price);
        Assert.True(small.InRange);

        AiItemPrice custom = ranged.Closest.Single(i => i.ProductId == quote);
        Assert.True(custom.PriceOnRequest);
        Assert.Null(custom.Price);
        Assert.Null(custom.InRange);
        Assert.Equal(AvailabilityMode.MadeToOrder, custom.Mode);
        Assert.Equal(7, custom.LeadTimeDays);

        // Only a lower limit.
        AiPricesResult upmarket = await PricesAsync(ai, new { ids = asked, min_price = 12000 });
        Assert.Equal([tote, sizes], upmarket.Items.Select(i => i.ProductId));
    }

    [PostgresFact]
    public async Task The_price_tool_refuses_empty_and_oversized_requests_and_other_scopes()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient ai = _fx.Client(workspace, CatalogScopes.AiTools);
        using HttpClient reader = _fx.Client(workspace, CatalogScopes.Read);

        Assert.Equal(HttpStatusCode.BadRequest, (await ai.PostAsync("/api/catalog/ai/tools/get_prices", CatalogApiFixture.Body(new { ids = Array.Empty<Guid>() }))).StatusCode);
        Guid[] tooMany = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await ai.PostAsync("/api/catalog/ai/tools/get_prices", CatalogApiFixture.Body(new { ids = tooMany }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ai.PostAsync("/api/catalog/ai/tools/get_prices", CatalogApiFixture.Body(new { ids = new[] { Guid.NewGuid() }, max_price = -1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ai.PostAsync("/api/catalog/ai/tools/get_prices", CatalogApiFixture.Body(new { ids = new[] { Guid.NewGuid() }, min_price = 5000, max_price = 1000 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/catalog/ai/tools/get_prices", CatalogApiFixture.Body(new { ids = new[] { Guid.NewGuid() } }))).StatusCode);
    }

    [PostgresFact]
    public async Task Another_workspaces_products_are_not_on_sale_here()
    {
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        using HttpClient theirOwner = _fx.Client(theirs, Owner);
        using HttpClient ai = _fx.Client(mine, CatalogScopes.AiTools);
        Guid theirBag = await AddAsync(theirOwner, "Their bag", new { mode = "single", amount = 4000 }, new { mode = "in_stock" });

        AiPricesResult result = await PricesAsync(ai, new { ids = new[] { theirBag } });

        Assert.Empty(result.Items);
        Assert.Equal(theirBag, Assert.Single(result.NotOnSale));
    }

    [PostgresFact]
    public async Task Hidden_and_draft_items_are_kept_from_search_and_every_ai_tool()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);
        using HttpClient ai = _fx.Client(workspace, CatalogScopes.AiTools);
        using HttpClient reader = _fx.Client(workspace, CatalogScopes.Read);

        Guid shown = await AddAsync(owner, "Raffia tote shown", new { mode = "single", amount = 4000 }, new { mode = "in_stock" });
        Guid hidden = await AddAsync(owner, "Raffia tote hidden", new { mode = "single", amount = 4000 }, new { mode = "in_stock" }, hide: true);
        Guid draft = await AddAsync(owner, "Raffia tote draft", new { mode = "single", amount = 4000 }, new { mode = "in_stock" }, status: "draft");

        HttpResponseMessage search = await ai.PostAsync("/api/catalog/ai/tools/search_catalog", CatalogApiFixture.Body(new { query = "raffia tote" }));
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        AiSearchResult found = (await CatalogApiFixture.ReadAsync<AiToolResponse<AiSearchResult>>(search)).Result;
        Assert.Equal(shown, Assert.Single(found.Items).Product.Id);

        foreach (Guid id in new[] { hidden, draft })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ai.PostAsync("/api/catalog/ai/tools/get_product", CatalogApiFixture.Body(new { id }))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await ai.PostAsync("/api/catalog/ai/tools/get_product_availability", CatalogApiFixture.Body(new { id }))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/catalog/products/{id}")).StatusCode);
        }
    }

    [PostgresFact]
    public async Task Availability_says_sold_out_rather_than_nothing_recorded()
    {
        Guid workspace = Guid.NewGuid();
        using HttpClient owner = _fx.Client(workspace, Owner);
        using HttpClient ai = _fx.Client(workspace, CatalogScopes.AiTools);

        Guid inStock = await AddAsync(owner, "Quilted bag", new { mode = "single", amount = 4000 }, new { mode = "in_stock", quantity = 5 });
        Guid soldOut = await AddAsync(owner, "Beaded purse", new { mode = "single", amount = 3000 }, new { mode = "in_stock", quantity = 0 });
        Guid switchedOff = await AddAsync(owner, "Linen pouch", new { mode = "single", amount = 2500 }, new { mode = "in_stock", available = false });
        Guid sizes = await AddAsync(owner, "Woven basket", new
        {
            mode = "variants",
            variants = new object[] { new { name = "Small", amount = 9000, available = false }, new { name = "Large", amount = 15000 } },
        }, new { mode = "in_stock" });
        Guid allGone = await AddAsync(owner, "Straw hat", new
        {
            mode = "variants",
            variants = new object[] { new { name = "Small", amount = 5000, available = false }, new { name = "Large", amount = 6000, available = false } },
        }, new { mode = "in_stock" });

        AiAvailabilityResult bag = await AvailabilityAsync(ai, inStock);
        Assert.True(bag.Available);
        Assert.Equal(5, bag.QuantityLeft);

        AiAvailabilityResult purse = await AvailabilityAsync(ai, soldOut);
        Assert.False(purse.Available);
        Assert.Equal(0, purse.QuantityLeft);
        Assert.False(Assert.Single(purse.Availability).Available);

        AiAvailabilityResult pouch = await AvailabilityAsync(ai, switchedOff);
        Assert.False(pouch.Available);
        Assert.Equal(0, pouch.QuantityLeft);
        Assert.NotEmpty(pouch.Availability);
        Assert.All(pouch.Availability, row => Assert.False(row.Available));

        AiAvailabilityResult basket = await AvailabilityAsync(ai, sizes);
        Assert.True(basket.Available);
        Assert.Contains(basket.Availability, row => row.VariantId is not null && !row.Available);

        AiAvailabilityResult hat = await AvailabilityAsync(ai, allGone);
        Assert.False(hat.Available);
        Assert.Equal(0, hat.QuantityLeft);
    }

    private static async Task<AiAvailabilityResult> AvailabilityAsync(HttpClient ai, Guid id)
    {
        HttpResponseMessage response = await ai.PostAsync("/api/catalog/ai/tools/get_product_availability", CatalogApiFixture.Body(new { id }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await CatalogApiFixture.ReadAsync<AiToolResponse<AiAvailabilityResult>>(response)).Result;
    }

    private static async Task<AiPricesResult> PricesAsync(HttpClient ai, object body)
    {
        HttpResponseMessage response = await ai.PostAsync("/api/catalog/ai/tools/get_prices", CatalogApiFixture.Body(body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await CatalogApiFixture.ReadAsync<AiToolResponse<AiPricesResult>>(response)).Result;
    }

    private static async Task<Guid> AddAsync(HttpClient owner, string name, object pricing, object availability, string status = "published", bool hide = false)
    {
        HttpResponseMessage response = await owner.PostAsync("/api/catalog/items", CatalogApiFixture.Body(new
        {
            name,
            kind = "product",
            pricing,
            availability,
            status,
            hidden = hide,
        }));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await CatalogApiFixture.ReadAsync<ItemResponse>(response)).Id;
    }
}
