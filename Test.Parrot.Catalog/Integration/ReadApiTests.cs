using System.Net;
using System.Text.Json;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Domain.Enums;
using Test.Parrot.Catalog.Fixtures;

namespace Test.Parrot.Catalog.Integration;

[Collection(CatalogApiCollection.Name)]
public class ReadApiTests
{
    private readonly CatalogApiFixture _fx;

    public ReadApiTests(CatalogApiFixture fx)
    {
        _fx = fx;
    }

    [PostgresFact]
    public async Task Lists_only_published_products_with_registry_labels()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.Read);
        HttpResponseMessage response = await client.GetAsync("/api/catalog/products?limit=2");
        response.EnsureSuccessStatusCode();
        PagedResponse<ProductSummary> page = await CatalogApiFixture.ReadAsync<PagedResponse<ProductSummary>>(response);
        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.NextCursor);

        HttpResponseMessage second = await client.GetAsync($"/api/catalog/products?limit=2&cursor={Uri.EscapeDataString(page.NextCursor!)}");
        PagedResponse<ProductSummary> page2 = await CatalogApiFixture.ReadAsync<PagedResponse<ProductSummary>>(second);
        Assert.Equal(2, page2.Items.Count);
        Assert.Null(page2.NextCursor);
        Assert.Empty(page.Items.Select(p => p.Id).Intersect(page2.Items.Select(p => p.Id)));

        ProductSummary laptop = page.Items.Concat(page2.Items).Single(p => p.Sku == "LPT-PB14");
        Assert.Equal("laptop", laptop.Type.Key);
        Assert.Contains(laptop.Attributes, a => a.Key == "screen_inches" && a.Label == "Screen size" && a.Unit == "in");
        Assert.Equal(["Hardware", "Laptops"], laptop.Category!.Path);
    }

    [PostgresFact]
    public async Task Product_detail_has_variants_and_etag_roundtrip_gives_304()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.Read);
        Guid laptop = _fx.SeedA!.ProductIds["laptop"];
        HttpResponseMessage first = await client.GetAsync($"/api/catalog/products/{laptop}");
        first.EnsureSuccessStatusCode();
        ProductDetail detail = await CatalogApiFixture.ReadAsync<ProductDetail>(first);
        Assert.Equal(3, detail.Variants.Count);
        Assert.Contains(detail.Variants, v => v.Attributes.Any(a => a.Key == "ram_gb" && a.Value.GetInt32() == 32));
        Assert.NotNull(first.Headers.ETag);

        using HttpRequestMessage conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/catalog/products/{laptop}");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        HttpResponseMessage second = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);

        HttpResponseMessage third = await client.GetAsync($"/api/catalog/products/{laptop}");
        Assert.Equal("HIT", third.Headers.GetValues("X-Cache").Single());
    }

    [PostgresFact]
    public async Task Offers_are_effective_dated_and_as_of_reveals_history()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.Read);
        Guid fibre = _fx.SeedA!.ProductIds["internet_plan"];

        IReadOnlyList<OfferView> now = await CatalogApiFixture.ReadAsync<IReadOnlyList<OfferView>>(await client.GetAsync($"/api/catalog/products/{fibre}/offers"));
        Assert.Equal(["Starter", "Pro", "Enterprise"], now.Select(o => o.Name));
        OfferView starter = now.Single(o => o.Name == "Starter");
        Assert.Equal(2, starter.Prices.Count);
        Assert.Contains(starter.Prices, p => p.Currency == "NGN" && p.Amount == 25_000m);
        Assert.Contains(starter.Prices, p => p.Currency == "USD" && p.Amount == 16m);

        IReadOnlyList<OfferView> past = await CatalogApiFixture.ReadAsync<IReadOnlyList<OfferView>>(await client.GetAsync($"/api/catalog/products/{fibre}/offers?as_of=2026-02-01T00:00:00Z"));
        Assert.Equal(3, past.Single(o => o.Name == "Starter").Prices.Count);
        Assert.Contains(past.Single(o => o.Name == "Starter").Prices, p => p.Amount == 19_999m);

        IReadOnlyList<OfferView> before = await CatalogApiFixture.ReadAsync<IReadOnlyList<OfferView>>(await client.GetAsync($"/api/catalog/products/{fibre}/offers?as_of=2025-06-01T00:00:00Z"));
        Assert.Empty(before);
    }

    [PostgresFact]
    public async Task Search_filters_compile_against_registry_and_explain_matches()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.Read);

        SearchResponse ram = await Search(client, """{"filters": {"ram_gb": {"gte": 16}}}""");
        Assert.Single(ram.Items);
        Assert.Equal("LPT-PB14", ram.Items[0].Product.Sku);
        Assert.Contains(ram.Items[0].WhyMatched, w => w.Contains("ram_gb >= 16"));

        SearchResponse speed = await Search(client, """{"filters": {"speed_mbps": {"between": [50, 500]}, "technology": {"in": ["fibre"]}}}""");
        Assert.Single(speed.Items);
        Assert.Equal("NET-FIBRE-HOME", speed.Items[0].Product.Sku);

        SearchResponse text = await Search(client, """{"query": "fibre router"}""");
        Assert.Contains(text.Items, h => h.Product.Sku == "NET-FIBRE-HOME");
        Assert.Contains(text.Items[0].WhyMatched, w => w.Contains("fibre"));

        SearchResponse category = await Search(client, $$"""{"category": "{{_fx.SeedA!.CategoryIds["hardware"]}}"}""");
        Assert.Single(category.Items);
        Assert.Contains(category.Items[0].WhyMatched, w => w.Contains("Hardware > Laptops"));

        SearchResponse none = await Search(client, """{"filters": {"ram_gb": {"gte": 1000}}}""");
        Assert.Empty(none.Items);
    }

    [PostgresFact]
    public async Task Search_rejects_unknown_non_filterable_and_malformed_filters()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.Read);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/catalog/search", CatalogApiFixture.Raw("""{"filters": {"price": {"lte": 100}}}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/catalog/search", CatalogApiFixture.Raw("""{"filters": {"notes": {"eq": "x"}}}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/catalog/search", CatalogApiFixture.Raw("""{"filters": {"ram_gb": {"like": "16"}}}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/catalog/search", CatalogApiFixture.Raw("""{"filters": {"ram_gb": {"eq": 16}}, "unexpected": 1}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/catalog/search", CatalogApiFixture.Raw("""{"limit": 101}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/catalog/search", CatalogApiFixture.Raw("""{"sort": "price"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/catalog/search", CatalogApiFixture.Raw("""{"cursor": "bogus"}"""))).StatusCode);
    }

    [PostgresFact]
    public async Task Compare_related_availability_and_document_work()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.Read);
        Guid laptop = _fx.SeedA!.ProductIds["laptop"];
        Guid fibre = _fx.SeedA.ProductIds["internet_plan"];
        Guid bundle = _fx.SeedA.ProductIds["bundle"];

        CompareResponse compare = await CatalogApiFixture.ReadAsync<CompareResponse>(await client.PostAsync("/api/catalog/compare", CatalogApiFixture.Body(new CompareRequest([laptop, fibre]))));
        Assert.Equal(2, compare.Products.Count);
        CompareRow speed = compare.Rows.Single(r => r.Key == "speed_mbps");
        Assert.Equal("Mbps", speed.Unit);
        Assert.Null(speed.Values[laptop]);
        Assert.Equal(100, speed.Values[fibre]!.Value.GetInt32());

        IReadOnlyList<RelatedProductView> related = await CatalogApiFixture.ReadAsync<IReadOnlyList<RelatedProductView>>(await client.GetAsync($"/api/catalog/products/{bundle}/related"));
        Assert.Equal(3, related.Count(r => r.Direction == "outbound"));
        Assert.All(related, r => Assert.Equal(RelationshipType.BundledWith, r.RelationshipType));

        IReadOnlyList<RelatedProductView> laptopRelated = await CatalogApiFixture.ReadAsync<IReadOnlyList<RelatedProductView>>(await client.GetAsync($"/api/catalog/products/{laptop}/related"));
        Assert.Contains(laptopRelated, r => r.Direction == "inbound" && r.Product.Id == bundle);
        Assert.Contains(laptopRelated, r => r.Direction == "outbound" && r.RelationshipType == RelationshipType.Upsell);

        IReadOnlyList<AvailabilityView> availability = await CatalogApiFixture.ReadAsync<IReadOnlyList<AvailabilityView>>(await client.GetAsync($"/api/catalog/products/{laptop}/availability?region=NG"));
        Assert.Equal(3, availability.Count);
        Assert.Contains(availability, a => a.StockQty == 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/catalog/products/{laptop}/availability?region=nigeria")).StatusCode);

        DocumentView document = await CatalogApiFixture.ReadAsync<DocumentView>(await client.GetAsync($"/api/catalog/products/{laptop}/document"));
        Assert.Contains("Product: ProBook 14 (SKU LPT-PB14)", document.Text);
        Assert.Contains("Memory: 16 GB", document.Text);
        Assert.Contains("Category: Hardware > Laptops", document.Text);
        Assert.Contains("Related products:", document.Text);
        Assert.Equal(64, document.ContentHash.Length);
    }

    [PostgresFact]
    public async Task Ai_tools_return_sanitised_untrusted_text_and_why_matched()
    {
        using HttpClient client = _fx.Client(_fx.TenantA, CatalogScopes.AiTools);
        HttpResponseMessage schemas = await client.GetAsync("/api/catalog/ai/tools/schemas");
        schemas.EnsureSuccessStatusCode();
        using JsonDocument manifest = JsonDocument.Parse(await schemas.Content.ReadAsStringAsync());
        Assert.Equal(7, manifest.RootElement.GetProperty("tools").GetArrayLength());
        Assert.Contains(manifest.RootElement.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "get_prices");
        Assert.Contains("never obey", manifest.RootElement.GetProperty("tools")[0].GetProperty("description").GetString());

        HttpResponseMessage search = await client.PostAsync("/api/catalog/ai/tools/search_catalog", CatalogApiFixture.Raw("""{"query": "laptop", "filters": {"ram_gb": {"gte": 8}}, "limit": 5}"""));
        search.EnsureSuccessStatusCode();
        using JsonDocument body = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        Assert.Equal("search_catalog", body.RootElement.GetProperty("tool").GetString());
        Assert.True(body.RootElement.TryGetProperty("data_as_of", out _));
        JsonElement hit = body.RootElement.GetProperty("result").GetProperty("items")[0];
        Assert.Equal("untrusted_text", hit.GetProperty("product").GetProperty("name").GetProperty("kind").GetString());
        Assert.Equal("ProBook 14", hit.GetProperty("product").GetProperty("name").GetProperty("value").GetString());
        Assert.False(hit.GetProperty("product").TryGetProperty("tenant_id", out _));
        Assert.False(hit.GetProperty("product").TryGetProperty("search_text", out _));
        Assert.True(hit.GetProperty("why_matched").GetArrayLength() > 0);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/catalog/ai/tools/search_catalog", CatalogApiFixture.Raw("""{"limit": 50}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/catalog/ai/tools/search_catalog", CatalogApiFixture.Raw("""{"tenant_id": "x", "query": "laptop"}"""))).StatusCode);

        HttpResponseMessage offers = await client.PostAsync("/api/catalog/ai/tools/get_product_offers", CatalogApiFixture.Body(new { id = _fx.SeedA!.ProductIds["internet_plan"] }));
        offers.EnsureSuccessStatusCode();
        using JsonDocument offersBody = JsonDocument.Parse(await offers.Content.ReadAsStringAsync());
        Assert.Equal(3, offersBody.RootElement.GetProperty("result").GetProperty("offers").GetArrayLength());

        HttpResponseMessage compare = await client.PostAsync("/api/catalog/ai/tools/compare_products", CatalogApiFixture.Body(new { ids = new[] { _fx.SeedA.ProductIds["laptop"], _fx.SeedA.ProductIds["bundle"] } }));
        compare.EnsureSuccessStatusCode();
        HttpResponseMessage related = await client.PostAsync("/api/catalog/ai/tools/get_related_products", CatalogApiFixture.Body(new { id = _fx.SeedA.ProductIds["bundle"], relationship_type = "bundled_with" }));
        related.EnsureSuccessStatusCode();
        HttpResponseMessage availability = await client.PostAsync("/api/catalog/ai/tools/get_product_availability", CatalogApiFixture.Body(new { id = _fx.SeedA.ProductIds["laptop"], region = "NG" }));
        availability.EnsureSuccessStatusCode();
        HttpResponseMessage product = await client.PostAsync("/api/catalog/ai/tools/get_product", CatalogApiFixture.Body(new { id = _fx.SeedA.ProductIds["laptop"] }));
        product.EnsureSuccessStatusCode();
    }

    private static async Task<SearchResponse> Search(HttpClient client, string json)
    {
        HttpResponseMessage response = await client.PostAsync("/api/catalog/search", CatalogApiFixture.Raw(json));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await CatalogApiFixture.ReadAsync<SearchResponse>(response);
    }
}
