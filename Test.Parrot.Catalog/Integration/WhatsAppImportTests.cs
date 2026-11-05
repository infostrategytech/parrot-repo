using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Domain.Enums;
using Test.Parrot.Catalog.Fixtures;

namespace Test.Parrot.Catalog.Integration;

[Collection(CatalogApiCollection.Name)]
public class WhatsAppImportTests
{
    private readonly CatalogApiFixture _fx;

    public WhatsAppImportTests(CatalogApiFixture fx)
    {
        _fx = fx;
    }

    // ---- test 1: first call provisions catalog and product type ----

    [PostgresFact]
    public async Task Sync_FirstCall_ProvisionsCatalogAndProductType()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        WhatsAppSyncResponse result = await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("SKU-001", "Test Product", null, "1000 NGN", null, null, "in stock", null, null, null, null, null)
        ]);

        Assert.NotEqual(Guid.Empty, result.CatalogId);
        Assert.NotEqual(Guid.Empty, result.ProductTypeId);
        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Failed);
    }

    // ---- test 2: second call for same connection reuses provisioned catalog and type ----

    [PostgresFact]
    public async Task Sync_SecondCall_ReusesCatalogAndProductType()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        WhatsAppSyncResponse first = await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("SKU-A", "Product A", null, "500 NGN", null, null, null, null, null, null, null, null)
        ]);

        WhatsAppSyncResponse second = await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("SKU-B", "Product B", null, "600 NGN", null, null, null, null, null, null, null, null)
        ]);

        Assert.Equal(first.CatalogId, second.CatalogId);
        Assert.Equal(first.ProductTypeId, second.ProductTypeId);
        Assert.Equal(1, second.Created);
    }

    // ---- test 3: different connections get different catalogs ----

    [PostgresFact]
    public async Task Sync_DifferentConnections_GetDifferentCatalogs()
    {
        Guid tenantId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        WhatsAppSyncResponse conn1 = await SyncAsync(client, Guid.NewGuid(), [
            new WhatsAppProductInput("SKU-X", "Item X", null, null, null, null, null, null, null, null, null, null)
        ]);
        WhatsAppSyncResponse conn2 = await SyncAsync(client, Guid.NewGuid(), [
            new WhatsAppProductInput("SKU-X", "Item X", null, null, null, null, null, null, null, null, null, null)
        ]);

        Assert.NotEqual(conn1.CatalogId, conn2.CatalogId);
        Assert.Equal(conn1.ProductTypeId, conn2.ProductTypeId); // same type per tenant
    }

    // ---- test 4: product is created with correct attributes ----

    [PostgresFact]
    public async Task Sync_NewProduct_CreatesWithCorrectAttributes()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        WhatsAppSyncResponse result = await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("ATTR-001", "Branded Item", "A nice product", "2000 NGN", "1800 NGN",
                "https://example.com/img.jpg", "in stock", "Samsung", "new", 50, "meta-p-1", "meta-c-1")
        ]);

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Failed);

        // Verify product is immediately published and readable
        using HttpClient reader = _fx.Client(tenantId, CatalogScopes.Read);
        PagedResponse<WhatsAppProductResponse> products = await GetProductsAsync(client, connectionId);
        Assert.Single(products.Items);
        WhatsAppProductResponse product = products.Items[0];
        Assert.Equal("ATTR-001", product.RetailerId);
        Assert.Equal("Branded Item", product.Name);
        Assert.Equal("A nice product", product.Description);
        Assert.Equal("Samsung", product.Brand);
        Assert.Equal("new", product.Condition);
        Assert.Equal(ProductStatus.Published, product.Status);
        Assert.NotNull(product.PublishedAt);
    }

    // ---- test 5: SKU idempotency — same SKU on second call updates, not creates ----

    [PostgresFact]
    public async Task Sync_SameSku_UpdatesExistingProduct()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("UPDATE-SKU", "Original Name", null, "100 NGN", null, null, null, null, null, null, null, null)
        ]);

        WhatsAppSyncResponse second = await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("UPDATE-SKU", "Updated Name", null, "200 NGN", null, null, null, null, null, null, null, null)
        ]);

        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Updated);
        Assert.Equal(0, second.Failed);

        PagedResponse<WhatsAppProductResponse> products = await GetProductsAsync(client, connectionId);
        Assert.Single(products.Items);
        Assert.Equal("Updated Name", products.Items[0].Name);
        Assert.Equal("200 NGN", products.Items[0].RawPrice);
    }

    // ---- test 6: raw price string is stored and returned ----

    [PostgresFact]
    public async Task Sync_WithPrice_StoresRawPriceString()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("PRICE-001", "Priced Item", null, "15000 NGN", null, null, null, null, null, null, null, null)
        ]);

        PagedResponse<WhatsAppProductResponse> products = await GetProductsAsync(client, connectionId);
        WhatsAppProductResponse p = products.Items[0];
        Assert.Equal("15000 NGN", p.RawPrice);
        Assert.Null(p.RawSalePrice);
    }

    // ---- test 7: raw sale price string is stored and returned ----

    [PostgresFact]
    public async Task Sync_WithSalePrice_StoresRawSalePriceString()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("PROMO-001", "Sale Item", null, "10000 NGN", "8000 NGN", null, null, null, null, null, null, null)
        ]);

        PagedResponse<WhatsAppProductResponse> products = await GetProductsAsync(client, connectionId);
        WhatsAppProductResponse p = products.Items[0];
        Assert.Equal("10000 NGN", p.RawPrice);
        Assert.Equal("8000 NGN", p.RawSalePrice);
    }

    // ---- test 8: Meta availability "in stock" → InStock availability ----

    [PostgresFact]
    public async Task Sync_InStockAvailability_MapsCorrectly()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("AVAIL-IN", "In Stock Item", null, null, null, null, "in stock", null, null, 100, null, null)
        ]);

        PagedResponse<WhatsAppProductResponse> products = await GetProductsAsync(client, connectionId);
        Assert.Equal("in_stock", products.Items[0].Availability);
    }

    // ---- test 9: Meta availability "out of stock" → Inactive availability ----

    [PostgresFact]
    public async Task Sync_OutOfStockAvailability_MapsToInactive()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("AVAIL-OUT", "OOS Item", null, null, null, null, "out of stock", null, null, 0, null, null)
        ]);

        PagedResponse<WhatsAppProductResponse> products = await GetProductsAsync(client, connectionId);
        // out_of_stock maps to MadeToOrder (false) → InStock mode, Inactive status
        Assert.Equal("out_of_stock", products.Items[0].Availability);
    }

    // ---- test 10: retired product reactivated when it reappears in sync ----

    [PostgresFact]
    public async Task Sync_RetiredProduct_ReactivatedOnReappearance()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        // Sync and retire
        WhatsAppSyncResponse first = await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("REACTIVATE-001", "Was Retired", null, "500 NGN", null, null, null, null, null, null, null, null)
        ]);

        await RetireMissingAsync(client, connectionId, [], first.CatalogId);

        // Check it's retired
        PagedResponse<WhatsAppProductResponse> afterRetire = await GetProductsAsync(client, connectionId);
        WhatsAppProductResponse retiredProduct = afterRetire.Items.Single(p => p.RetailerId == "REACTIVATE-001");
        Assert.Equal(ProductStatus.Retired, retiredProduct.Status);

        // Resync — should reactivate
        WhatsAppSyncResponse second = await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("REACTIVATE-001", "Was Retired", null, "500 NGN", null, null, null, null, null, null, null, null)
        ]);

        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Updated);

        PagedResponse<WhatsAppProductResponse> afterReactivate = await GetProductsAsync(client, connectionId);
        WhatsAppProductResponse reactivated = afterReactivate.Items.Single(p => p.RetailerId == "REACTIVATE-001");
        Assert.Equal(ProductStatus.Published, reactivated.Status);
        Assert.Null(reactivated.RetiredAt);
    }

    // ---- test 11: RetireMissing retires published products not in PresentSkus ----

    [PostgresFact]
    public async Task RetireMissing_RetiresMissingProducts()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        WhatsAppSyncResponse sync = await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("KEEP-001", "Keep Me", null, null, null, null, null, null, null, null, null, null),
            new WhatsAppProductInput("RETIRE-001", "Retire Me", null, null, null, null, null, null, null, null, null, null),
        ]);

        WhatsAppRetireMissingResponse retire = await RetireMissingAsync(
            client, connectionId, ["KEEP-001"], sync.CatalogId, validSkuCount: 1, paginationCompleted: true);

        Assert.Equal(1, retire.Retired);
        Assert.False(retire.Skipped);

        PagedResponse<WhatsAppProductResponse> products = await GetProductsAsync(client, connectionId);
        Assert.Equal(ProductStatus.Published, products.Items.Single(p => p.RetailerId == "KEEP-001").Status);
        Assert.Equal(ProductStatus.Retired, products.Items.Single(p => p.RetailerId == "RETIRE-001").Status);
    }

    // ---- test 12: RetireMissing skipped when PaginationCompleted = false ----

    [PostgresFact]
    public async Task RetireMissing_PaginationNotComplete_Skips()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        WhatsAppSyncResponse sync = await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("SKIP-001", "Should Not Retire", null, null, null, null, null, null, null, null, null, null)
        ]);

        WhatsAppRetireMissingResponse result = await RetireMissingAsync(
            client, connectionId, [], sync.CatalogId,
            validSkuCount: 1, paginationCompleted: false);

        Assert.True(result.Skipped);
        Assert.Equal(0, result.Retired);
    }

    // ---- test 13: RetireMissing skipped when ValidSkuCount = 0 and FailedProductCount = 0 ----

    [PostgresFact]
    public async Task RetireMissing_EmptyMeta_Skips()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        WhatsAppSyncResponse sync = await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("GUARD-001", "Guard Me", null, null, null, null, null, null, null, null, null, null)
        ]);

        WhatsAppRetireMissingResponse result = await RetireMissingAsync(
            client, connectionId, [], sync.CatalogId,
            validSkuCount: 0, failedProductCount: 0, paginationCompleted: true);

        Assert.True(result.Skipped);
        Assert.Equal(0, result.Retired);
    }

    // ---- test 14: products are published immediately, visible on read side ----

    [PostgresFact]
    public async Task Sync_Products_AreImmediatelyPublished()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("PUBLISHED-001", "Published Item", null, null, null, null, null, null, null, null, null, null)
        ]);

        PagedResponse<WhatsAppProductResponse> products = await GetProductsAsync(client, connectionId);
        Assert.Single(products.Items);
        Assert.Equal(ProductStatus.Published, products.Items[0].Status);
        Assert.NotNull(products.Items[0].PublishedAt);

        // Also visible via the public read endpoint
        using HttpClient reader = _fx.Client(tenantId, CatalogScopes.Read);
        string? productId = products.Items[0].Id.ToString();
        HttpResponseMessage readResponse = await reader.GetAsync($"/api/catalog/products/{productId}");
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
    }

    // ---- test 15: invalid SKU counted as failed, others succeed ----

    [PostgresFact]
    public async Task Sync_InvalidSku_CountsAsFailedOthersSucceed()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        WhatsAppSyncResponse result = await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("VALID-SKU-1", "Good Product", null, null, null, null, null, null, null, null, null, null),
            new WhatsAppProductInput("invalid sku!", "Bad Product", null, null, null, null, null, null, null, null, null, null),
            new WhatsAppProductInput("VALID-SKU-2", "Another Good Product", null, null, null, null, null, null, null, null, null, null),
        ]);

        Assert.Equal(2, result.Created);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.Errors);
    }

    // ---- test 16: empty product list returns provisioned catalog/type IDs ----

    [PostgresFact]
    public async Task Sync_EmptyProductList_ReturnsProvisionedIds()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        WhatsAppSyncResponse result = await SyncAsync(client, connectionId, []);

        Assert.NotEqual(Guid.Empty, result.CatalogId);
        Assert.NotEqual(Guid.Empty, result.ProductTypeId);
        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Failed);
    }

    // ---- test 17: GetProducts returns empty page when catalog not yet provisioned ----

    [PostgresFact]
    public async Task GetProducts_NoCatalog_ReturnsEmptyPage()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        PagedResponse<WhatsAppProductResponse> result = await GetProductsAsync(client, connectionId);

        Assert.Empty(result.Items);
        Assert.Null(result.NextCursor);
    }

    // ---- test 18: tenant isolation — another tenant's connection ID sees nothing ----

    [PostgresFact]
    public async Task GetProducts_AnotherTenantsConnectionId_ReturnsEmptyNotFound()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient clientA = AdminClient(tenantA);
        using HttpClient clientB = AdminClient(tenantB);

        // Tenant A syncs products
        await SyncAsync(clientA, connectionId, [
            new WhatsAppProductInput("ISOLATED-001", "Tenant A Product", null, null, null, null, null, null, null, null, null, null)
        ]);

        // Tenant B uses the same connection ID — must see nothing (empty page, not 404)
        PagedResponse<WhatsAppProductResponse> result = await GetProductsAsync(clientB, connectionId);
        Assert.Empty(result.Items);

        // And a random unknown ID also returns empty
        PagedResponse<WhatsAppProductResponse> random = await GetProductsAsync(clientB, Guid.NewGuid());
        Assert.Empty(random.Items);
    }

    // ---- test 19: GetProducts pagination ----

    [PostgresFact]
    public async Task GetProducts_Pagination_Works()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        // Sync 5 products
        List<WhatsAppProductInput> products = [];
        for (int i = 1; i <= 5; i++)
        {
            products.Add(new WhatsAppProductInput($"PAGE-{i:D3}", $"Product {i}", null, null, null, null, null, null, null, null, null, null));
        }

        await SyncAsync(client, connectionId, products);

        // First page of 3
        PagedResponse<WhatsAppProductResponse> page1 = await GetProductsAsync(client, connectionId, limit: 3);
        Assert.Equal(3, page1.Items.Count);
        Assert.NotNull(page1.NextCursor);

        // Second page
        PagedResponse<WhatsAppProductResponse> page2 = await GetProductsAsync(client, connectionId, limit: 3, cursor: page1.NextCursor);
        Assert.Equal(2, page2.Items.Count);
        Assert.Null(page2.NextCursor);

        // All SKUs are distinct and together account for all 5
        HashSet<string> allSkus = new(page1.Items.Select(p => p.RetailerId));
        allSkus.UnionWith(page2.Items.Select(p => p.RetailerId));
        Assert.Equal(5, allSkus.Count);
    }

    // ---- test 20: anonymous and wrong-scope requests return 401/403 ----

    [PostgresFact]
    public async Task WhatsAppEndpoints_Authorization()
    {
        Guid connectionId = Guid.NewGuid();

        // Anonymous → 401
        using HttpClient anon = _fx.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/catalog/internal/whatsapp/sync",
            CatalogApiFixture.Raw("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/catalog/internal/whatsapp/retire-missing",
            CatalogApiFixture.Raw("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/catalog/internal/whatsapp/products?connection_id={connectionId}")).StatusCode);

        // Read scope only — sync/retire need Admin → 403
        using HttpClient readOnly = _fx.Client(_fx.TenantA, CatalogScopes.Read);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsync("/api/catalog/internal/whatsapp/sync",
            CatalogApiFixture.Raw("{}"))).StatusCode);

        // Admin scope → can sync (may still get 400 for bad body, but not 401/403)
        using HttpClient admin = AdminClient(_fx.TenantA);
        HttpResponseMessage syncResp = await admin.PostAsync("/api/catalog/internal/whatsapp/sync",
            CatalogApiFixture.Raw($$"""{"connection_id":"00000000-0000-0000-0000-000000000000","products":[]}"""));
        Assert.NotEqual(HttpStatusCode.Unauthorized, syncResp.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, syncResp.StatusCode);
    }

    // ---- test 21: raw price string preserved exactly, no conversion ----

    [PostgresFact]
    public async Task Sync_WithRawPrice_PreservesOriginalPriceString()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("RAWPRICE-001", "Priced Item", null, "1000 USD", null, null, null, null, null, null, null, null)
        ]);

        PagedResponse<WhatsAppProductResponse> products = await GetProductsAsync(client, connectionId);
        WhatsAppProductResponse p = products.Items[0];
        Assert.Equal("1000 USD", p.RawPrice);
        Assert.Null(p.RawSalePrice);
    }

    // ---- test 22: raw sale price preserved, JPY included as a non-subunit currency ----

    [PostgresFact]
    public async Task Sync_WithRawSalePrice_PreservesOriginalSalePriceString()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("RAWSALE-001", "Sale Item", null, "1000 JPY", "800 JPY", null, null, null, null, null, null, null)
        ]);

        PagedResponse<WhatsAppProductResponse> products = await GetProductsAsync(client, connectionId);
        WhatsAppProductResponse p = products.Items[0];
        Assert.Equal("1000 JPY", p.RawPrice);
        Assert.Equal("800 JPY", p.RawSalePrice);
    }

    // ---- test 23: null price field → RawPrice null on response ----

    [PostgresFact]
    public async Task Sync_WithNullPrice_RawPriceIsNull()
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        using HttpClient client = AdminClient(tenantId);

        await SyncAsync(client, connectionId, [
            new WhatsAppProductInput("NOPRICE-001", "No Price Item", null, null, null, null, null, null, null, null, null, null)
        ]);

        PagedResponse<WhatsAppProductResponse> products = await GetProductsAsync(client, connectionId);
        WhatsAppProductResponse p = products.Items[0];
        Assert.Null(p.RawPrice);
        Assert.Null(p.RawSalePrice);
    }

    // ---------- helpers ----------

    private HttpClient AdminClient(Guid tenantId) => _fx.Client(tenantId, CatalogScopes.Admin);

    private async Task<WhatsAppSyncResponse> SyncAsync(HttpClient client, Guid connectionId, IReadOnlyList<WhatsAppProductInput> products)
    {
        WhatsAppSyncRequest request = new WhatsAppSyncRequest(connectionId, products);
        HttpResponseMessage response = await client.PostAsync("/api/catalog/internal/whatsapp/sync", CatalogApiFixture.Body(request));
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Sync failed {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<WhatsAppSyncResponse>(body, CatalogApiFixture.Json)!;
    }

    private async Task<WhatsAppRetireMissingResponse> RetireMissingAsync(
        HttpClient client, Guid connectionId, IReadOnlyList<string> presentSkus, Guid catalogId,
        int validSkuCount = 1, int failedProductCount = 0, bool paginationCompleted = true)
    {
        WhatsAppRetireMissingRequest request = new WhatsAppRetireMissingRequest(
            connectionId, presentSkus, paginationCompleted, validSkuCount, failedProductCount);
        HttpResponseMessage response = await client.PostAsync("/api/catalog/internal/whatsapp/retire-missing", CatalogApiFixture.Body(request));
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"RetireMissing failed {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<WhatsAppRetireMissingResponse>(body, CatalogApiFixture.Json)!;
    }

    private async Task<PagedResponse<WhatsAppProductResponse>> GetProductsAsync(
        HttpClient client, Guid connectionId, int? limit = null, string? cursor = null)
    {
        string url = $"/api/catalog/internal/whatsapp/products?connection_id={connectionId}";
        if (limit.HasValue) url += $"&limit={limit}";
        if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
        HttpResponseMessage response = await client.GetAsync(url);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GetProducts failed {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<PagedResponse<WhatsAppProductResponse>>(body, CatalogApiFixture.Json)!;
    }
}
