using System.Net;
using System.Text.Json;
using Npgsql;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Tools.Commands;
using Test.Parrot.Catalog.Fixtures;

namespace Test.Parrot.Catalog.Integration;

/// <summary>
/// Tenant B holds valid tokens with every scope and knows tenant A's ids. Through every
/// endpoint and every AI tool, B must get 403/404 or an empty result, never A's data.
/// </summary>
[Collection(CatalogApiCollection.Name)]
public class TenantIsolationTests
{
    private readonly CatalogApiFixture _fx;

    public TenantIsolationTests(CatalogApiFixture fx)
    {
        _fx = fx;
    }

    private Guid Laptop => _fx.SeedA!.ProductIds["laptop"];

    [PostgresFact]
    public async Task Read_api_shows_tenant_b_nothing_of_tenant_a()
    {
        using HttpClient b = _fx.Client(_fx.TenantB, CatalogScopes.Read);

        PagedResponse<ProductSummary> list = await CatalogApiFixture.ReadAsync<PagedResponse<ProductSummary>>(await b.GetAsync("/api/catalog/products"));
        Assert.Empty(list.Items);

        foreach (string path in new[] { "", "/offers", "/availability", "/related", "/document" })
        {
            HttpResponseMessage response = await b.GetAsync($"/api/catalog/products/{Laptop}{path}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        HttpResponseMessage search = await b.PostAsync("/api/catalog/search", CatalogApiFixture.Raw("""{"query": "ProBook"}"""));
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Empty((await CatalogApiFixture.ReadAsync<SearchResponse>(search)).Items);

        // A's attribute keys are not even filterable from B's point of view: the registry is tenant-scoped.
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsync("/api/catalog/search", CatalogApiFixture.Raw("""{"filters": {"ram_gb": {"gte": 1}}}"""))).StatusCode);

        HttpResponseMessage compare = await b.PostAsync("/api/catalog/compare", CatalogApiFixture.Body(new CompareRequest([Laptop, _fx.SeedA!.ProductIds["internet_plan"]])));
        Assert.Equal(HttpStatusCode.NotFound, compare.StatusCode);

        HttpResponseMessage byCategory = await b.GetAsync($"/api/catalog/products?category_id={_fx.SeedA.CategoryIds["hardware"]}");
        Assert.Empty((await CatalogApiFixture.ReadAsync<PagedResponse<ProductSummary>>(byCategory)).Items);
    }

    [PostgresFact]
    public async Task Ai_tools_show_tenant_b_nothing_of_tenant_a()
    {
        using HttpClient b = _fx.Client(_fx.TenantB, CatalogScopes.AiTools);

        HttpResponseMessage search = await b.PostAsync("/api/catalog/ai/tools/search_catalog", CatalogApiFixture.Raw("""{"query": "ProBook"}"""));
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("result").GetProperty("count").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/ai/tools/get_product", CatalogApiFixture.Body(new { id = Laptop }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/ai/tools/get_product_offers", CatalogApiFixture.Body(new { id = Laptop }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/ai/tools/get_product_availability", CatalogApiFixture.Body(new { id = Laptop }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/ai/tools/compare_products", CatalogApiFixture.Body(new { ids = new[] { Laptop, _fx.SeedA!.ProductIds["bundle"] } }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/ai/tools/get_related_products", CatalogApiFixture.Body(new { id = Laptop }))).StatusCode);

        // Category keys are tenant-scoped too.
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsync("/api/catalog/ai/tools/search_catalog", CatalogApiFixture.Raw("""{"category": "laptops"}"""))).StatusCode);
    }

    [PostgresFact]
    public async Task Write_api_refuses_tenant_b_every_write_against_tenant_a_rows()
    {
        using HttpClient b = _fx.Client(_fx.TenantB, CatalogScopes.Admin);
        SeedResult a = _fx.SeedA!;

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/catalog/admin/products/{Laptop}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsync($"/api/catalog/admin/products/{Laptop}", CatalogApiFixture.Raw("""{"name":"hijacked","attributes":{"brand":"B"}}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/catalog/admin/products/{Laptop}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/catalog/admin/products/{Laptop}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/catalog/admin/products/{Laptop}/retire", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/catalog/admin/products/{Laptop}/variants", CatalogApiFixture.Raw("""{"sku":"B-V","name":"v","attributes":{"ram_gb":8,"storage_gb":1,"colour":"black"}}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsync($"/api/catalog/admin/products/{Laptop}/variants/{a.ProductIds["laptop_variant_16gb"]}", CatalogApiFixture.Raw("""{"name":"v","attributes":{"ram_gb":8,"storage_gb":1,"colour":"black"},"status":"active","sort_order":0}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/catalog/admin/products/{Laptop}/variants/{a.ProductIds["laptop_variant_16gb"]}")).StatusCode);

        // Creating rows that point at A's catalog, type, category or products.
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/admin/products", CatalogApiFixture.Raw($$"""{"catalog_id":"{{a.CatalogId}}","product_type_id":"{{a.TypeIds["laptop"]}}","sku":"B-1","name":"b","attributes":{} }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/admin/categories", CatalogApiFixture.Raw($$"""{"catalog_id":"{{a.CatalogId}}","key":"b_cat","name":"b"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/admin/offers", CatalogApiFixture.Raw($$"""{"product_id":"{{Laptop}}","name":"b","billing_period":"monthly","valid_from":"2026-01-01T00:00:00Z"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/admin/offers", CatalogApiFixture.Raw($$"""{"variant_id":"{{a.ProductIds["laptop_variant_16gb"]}}","name":"b","billing_period":"monthly","valid_from":"2026-01-01T00:00:00Z"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/admin/availability", CatalogApiFixture.Raw($$"""{"product_id":"{{Laptop}}","region":"NG","valid_from":"2026-01-01T00:00:00Z","stock_qty":1}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/admin/relationships", CatalogApiFixture.Raw($$"""{"source_id":"{{Laptop}}","target_id":"{{a.ProductIds["bundle"]}}","relationship_type":"upsell"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync("/api/catalog/admin/media", CatalogApiFixture.Raw($$"""{"product_id":"{{Laptop}}","kind":"image","s3_key":"tenants/{{_fx.TenantB}}/x.png","mime_type":"image/png"}"""))).StatusCode);

        // A's types, offers and categories by id.
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/catalog/admin/types/{a.TypeIds["laptop"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/catalog/admin/types/{a.TypeIds["laptop"]}/attributes", CatalogApiFixture.Raw("""{"key":"evil","label":"Evil","data_type":"string"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/catalog/admin/types/{a.TypeIds["laptop"]}/versions", CatalogApiFixture.Raw("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/catalog/admin/types/{a.TypeIds["laptop"]}/schema")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/catalog/admin/categories/{a.CategoryIds["laptops"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/catalog/admin/categories/{a.CategoryIds["laptops"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/catalog/admin/catalogs/{a.CatalogId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/catalog/admin/catalogs/{a.CatalogId}")).StatusCode);

        // B's own listings are empty, not A's.
        Assert.Empty(await CatalogApiFixture.ReadAsync<IReadOnlyList<ProductTypeResponse>>(await b.GetAsync("/api/catalog/admin/types")));
        Assert.Empty(await CatalogApiFixture.ReadAsync<IReadOnlyList<CatalogResponse>>(await b.GetAsync("/api/catalog/admin/catalogs")));
        Assert.Empty((await CatalogApiFixture.ReadAsync<PagedResponse<ProductAdminResponse>>(await b.GetAsync("/api/catalog/admin/products"))).Items);

        // And A still has everything.
        using HttpClient aClient = _fx.Client(_fx.TenantA, CatalogScopes.Read);
        ProductDetail detail = await CatalogApiFixture.ReadAsync<ProductDetail>(await aClient.GetAsync($"/api/catalog/products/{Laptop}"));
        Assert.Equal("ProBook 14", detail.Product.Name);
    }

    [PostgresFact]
    public async Task Database_roles_enforce_isolation_below_the_application()
    {
        // The reader role sees other tenants' rows through no view, and no base table at all.
        Assert.Equal(0, await _fx.Database.ScalarAsync<long>(_fx.Database.Reader, _fx.TenantB, "SELECT count(*) FROM catalog.v_products"));
        Assert.True(await _fx.Database.ScalarAsync<long>(_fx.Database.Reader, _fx.TenantA, "SELECT count(*) FROM catalog.v_products") >= 4);
        Assert.Equal(0, await _fx.Database.ScalarAsync<long>(_fx.Database.Reader, null, "SELECT count(*) FROM catalog.v_products"));
        PostgresException baseTable = await Assert.ThrowsAsync<PostgresException>(() => _fx.Database.ScalarAsync<long>(_fx.Database.Reader, _fx.TenantA, "SELECT count(*) FROM catalog.products"));
        Assert.Equal("42501", baseTable.SqlState);

        // The writer role cannot read or write another tenant's rows even with raw SQL.
        Assert.Equal(0, await _fx.Database.ScalarAsync<long>(_fx.Database.Writer, _fx.TenantB, "SELECT count(*) FROM catalog.products"));
        Assert.Equal(0, await _fx.Database.ScalarAsync<long>(_fx.Database.Writer, null, "SELECT count(*) FROM catalog.products"));
        PostgresException crossInsert = await Assert.ThrowsAsync<PostgresException>(() => _fx.Database.ExecAsync(_fx.Database.Writer, _fx.TenantB,
            $"INSERT INTO catalog.tenants (id, tenant_id, name, status, created_at, updated_at) VALUES ('{_fx.TenantA}', '{_fx.TenantA}', 'hijack', 'active', now(), now())"));
        Assert.Equal("42501", crossInsert.SqlState);
        await _fx.Database.ExecAsync(_fx.Database.Writer, _fx.TenantB, $"UPDATE catalog.products SET name = 'hijacked' WHERE id = '{Laptop}'");
        Assert.Equal("ProBook 14", await _fx.Database.ScalarAsync<string>(_fx.Database.Migrator, _fx.TenantA, $"SELECT name FROM catalog.products WHERE id = '{Laptop}'"));

        // The relay role sees every tenant's outbox but nothing else.
        Assert.True(await _fx.Database.ScalarAsync<long>(_fx.Database.Relay, null, "SELECT count(*) FROM catalog.outbox") > 0);
        PostgresException relayProducts = await Assert.ThrowsAsync<PostgresException>(() => _fx.Database.ScalarAsync<long>(_fx.Database.Relay, _fx.TenantA, "SELECT count(*) FROM catalog.products"));
        Assert.Equal("42501", relayProducts.SqlState);

        // A pooled connection does not carry the previous tenant: open as A, then as nobody.
        Assert.True(await _fx.Database.ScalarAsync<long>(_fx.Database.Writer, _fx.TenantA, "SELECT count(*) FROM catalog.products") >= 4);
        Assert.Equal(0, await _fx.Database.ScalarAsync<long>(_fx.Database.Writer, null, "SELECT count(*) FROM catalog.products"));
    }
}
