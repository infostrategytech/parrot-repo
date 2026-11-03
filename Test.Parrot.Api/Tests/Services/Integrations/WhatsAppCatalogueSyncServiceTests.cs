using Microsoft.AspNetCore.DataProtection;
using Moq;
using Parrot.Application.Contracts.WhatsAppCatalogue;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.SocialMediaIntegrations;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class WhatsAppCatalogueSyncServiceTests
{
    private readonly Mock<IWhatsAppConnectionRepository> _connectionRepo = new();
    private readonly FakeCatalogImportClient _catalogClient = new();
    private readonly FakeMetaCatalogueClient _metaClient = new();
    private readonly IWhatsAppTokenProtector _protector =
        new WhatsAppTokenProtector(new EphemeralDataProtectionProvider());

    private WhatsAppCatalogueSyncService CreateService() =>
        new(_connectionRepo.Object, _catalogClient, _metaClient, _protector,
            Mock.Of<IAppLogger<WhatsAppCatalogueSyncService>>());

    // ── Connection guard tests ───────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_WhenConnectionNotFound_ThrowsNotFoundException()
    {
        _connectionRepo.Setup(r => r.GetByOwnerAndIdAsync("ws", It.IsAny<Guid>()))
            .ReturnsAsync((WhatsAppConnection?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().SyncAsync("ws", Guid.NewGuid(), default));
    }

    [Fact]
    public async Task SyncAsync_WhenAnotherOwnersConnection_ThrowsNotFoundException()
    {
        // Identical response to not-found — no existence leak.
        _connectionRepo.Setup(r => r.GetByOwnerAndIdAsync("other-ws", It.IsAny<Guid>()))
            .ReturnsAsync((WhatsAppConnection?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().SyncAsync("other-ws", Guid.NewGuid(), default));
    }

    [Fact]
    public async Task SyncAsync_WhenConnectionIsDisabled_ThrowsValidationException()
    {
        WhatsAppConnection connection = Connection();
        connection.DisabledAt = DateTime.UtcNow;
        SetupConnection("ws", connection);

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateService().SyncAsync("ws", connection.Id, default));
    }

    [Fact]
    public async Task SyncAsync_WhenNoWabaId_ThrowsValidationException()
    {
        WhatsAppConnection connection = Connection();
        connection.WhatsAppBusinessAccountId = null;
        SetupConnection("ws", connection);

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateService().SyncAsync("ws", connection.Id, default));
    }

    [Fact]
    public async Task SyncAsync_WhenNoAccessToken_ThrowsValidationException()
    {
        WhatsAppConnection connection = Connection();
        connection.AccessTokenEncrypted = null;
        SetupConnection("ws", connection);

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateService().SyncAsync("ws", connection.Id, default));
    }

    // ── Sync delegation tests ────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_WhenNoCatalogueFound_ReturnsZeroResultWithoutCallingCatalogClient()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _metaClient.CatalogueId = null;

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.Null(result.CatalogueId);
        Assert.False(_catalogClient.SyncCalled);
    }

    [Fact]
    public async Task SyncAsync_WithProducts_DelegatesToCatalogClient()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _metaClient.Products = [Product("SKU-A", "Alpha"), Product("SKU-B", "Beta")];
        _catalogClient.SyncResult = new CatalogImportSyncResult(Created: 2, Updated: 0, Failed: 0);

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.True(_catalogClient.SyncCalled);
        Assert.Equal(2, _catalogClient.LastSyncedProducts!.Count);
        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal("cat-1", result.CatalogueId);
    }

    [Fact]
    public async Task SyncAsync_WithExistingProducts_CountsUpdatesFromCatalogClient()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _metaClient.Products = [Product("SKU-A", "Alpha Updated")];
        _catalogClient.SyncResult = new CatalogImportSyncResult(Created: 0, Updated: 1, Failed: 0);

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public async Task SyncAsync_WithProducts_PassesConnectionIdAndWorkspaceIdToCatalogClient()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _metaClient.Products = [Product("SKU-A", "Alpha")];

        await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal("ws", _catalogClient.LastSyncWorkspaceId);
        Assert.Equal(connection.Id, _catalogClient.LastSyncConnectionId);
    }

    [Fact]
    public async Task SyncAsync_WithProducts_CallsRetireMissingWithPresentSkus()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _metaClient.Products = [Product("SKU-A", "Alpha"), Product("SKU-B", "Beta")];

        await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.True(_catalogClient.RetireCalled);
        Assert.Equal(2, _catalogClient.LastRetirePresentSkus!.Count);
        Assert.Contains("SKU-A", _catalogClient.LastRetirePresentSkus);
        Assert.Contains("SKU-B", _catalogClient.LastRetirePresentSkus);
        Assert.True(_catalogClient.LastRetirePaginationCompleted);
    }

    [Fact]
    public async Task SyncAsync_RetiredCountMapsToDeletedInResult()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _metaClient.Products = [Product("SKU-A", "Alpha")];
        _catalogClient.RetireResult = 3;

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(3, result.Deleted);
    }

    [Fact]
    public async Task SyncAsync_WithMultiplePages_FetchesAllProductsBeforeSyncing()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _metaClient.Pages = [
            new(Products: [Product("SKU-A", "Alpha"), Product("SKU-B", "Beta")], NextCursor: "cursor-1"),
            new(Products: [Product("SKU-C", "Gamma")], NextCursor: null),
        ];

        await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(3, _catalogClient.LastSyncedProducts!.Count);
    }

    [Fact]
    public async Task SyncAsync_WhenAllProductsMissingRetailerId_CountsFailedAndDoesNotCallRetire()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _metaClient.Products = [Product("", "Bad 1"), Product("   ", "Bad 2")];

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(2, result.Failed);
        // No valid SKUs and failed > 0 → retire is skipped to avoid wiping the catalogue.
        Assert.False(_catalogClient.RetireCalled);
    }

    [Fact]
    public async Task SyncAsync_FailedFromMetaAndCatalogAreAddedTogether()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        // One bad Meta product (no retailer id) + one catalog failure
        _metaClient.Products = [Product("", "Bad"), Product("SKU-A", "Good")];
        _catalogClient.SyncResult = new CatalogImportSyncResult(Created: 0, Updated: 0, Failed: 1);

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(2, result.Failed); // 1 meta + 1 catalog
    }

    [Fact]
    public async Task SyncAsync_WithEmptyCatalogue_PassesEmptySkusToCatalogRetire()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _metaClient.Products = [];

        await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.True(_catalogClient.RetireCalled);
        Assert.Empty(_catalogClient.LastRetirePresentSkus ?? []);
    }

    // ── GetProductsAsync tests ───────────────────────────────────────────────

    [Fact]
    public async Task GetProductsAsync_WhenConnectionNotFound_ThrowsNotFoundException()
    {
        _connectionRepo.Setup(r => r.GetByOwnerAndIdAsync("ws", It.IsAny<Guid>()))
            .ReturnsAsync((WhatsAppConnection?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().GetProductsAsync("ws", Guid.NewGuid(), 1, 50, null, default));
    }

    [Fact]
    public async Task GetProductsAsync_WhenAnotherOwnersConnection_ThrowsNotFoundException()
    {
        _connectionRepo.Setup(r => r.GetByOwnerAndIdAsync("other-ws", It.IsAny<Guid>()))
            .ReturnsAsync((WhatsAppConnection?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().GetProductsAsync("other-ws", Guid.NewGuid(), 1, 50, null, default));
    }

    [Fact]
    public async Task GetProductsAsync_DelegatesToCatalogClient()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogClient.ProductsResult = new CatalogueItemsResponse([], 0, 1, 50);

        CatalogueItemsResponse response =
            await CreateService().GetProductsAsync("ws", connection.Id, 1, 50, null, default);

        Assert.True(_catalogClient.GetProductsCalled);
        Assert.Equal("ws", _catalogClient.LastGetProductsWorkspaceId);
        Assert.Equal(connection.Id, _catalogClient.LastGetProductsConnectionId);
    }

    [Fact]
    public async Task GetProductsAsync_CapsPageSizeAt100()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogClient.ProductsResult = new CatalogueItemsResponse([], 0, 1, 100);

        await CreateService().GetProductsAsync("ws", connection.Id, 1, 9999, null, default);

        Assert.Equal(100, _catalogClient.LastGetProductsPageSize);
    }

    [Fact]
    public async Task GetProductsAsync_PassesPageAndPageSizeToCatalogClient()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogClient.ProductsResult = new CatalogueItemsResponse([], 0, 2, 25);

        await CreateService().GetProductsAsync("ws", connection.Id, 2, 25, null, default);

        Assert.Equal(2, _catalogClient.LastGetProductsPage);
        Assert.Equal(25, _catalogClient.LastGetProductsPageSize);
    }

    [Fact]
    public async Task GetProductsAsync_PassesCursorToCatalogClient()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogClient.ProductsResult = new CatalogueItemsResponse([], 0, 1, 50);

        await CreateService().GetProductsAsync("ws", connection.Id, 1, 50, "next-page-token", default);

        Assert.Equal("next-page-token", _catalogClient.LastGetProductsCursor);
    }

    [Fact]
    public async Task GetProductsAsync_PropagatesNextCursorFromCatalogClient()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogClient.ProductsResult = new CatalogueItemsResponse([], 0, 1, 50, NextCursor: "page-2-token");

        CatalogueItemsResponse response =
            await CreateService().GetProductsAsync("ws", connection.Id, 1, 50, null, default);

        Assert.Equal("page-2-token", response.NextCursor);
    }

    [Fact]
    public async Task GetProductsAsync_ReturnsResponseFromCatalogClient()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        List<CatalogueItemResponse> items =
        [
            new(Guid.NewGuid(), "SKU-A", "Alpha", null, 9.99m, "USD",
                null, null, null, null, null, null, null, "whatsapp",
                DateTime.UtcNow, DateTime.UtcNow),
        ];
        _catalogClient.ProductsResult = new CatalogueItemsResponse(items, 1, 1, 50);

        CatalogueItemsResponse response =
            await CreateService().GetProductsAsync("ws", connection.Id, 1, 50, null, default);

        Assert.Equal(1, response.Total);
        CatalogueItemResponse first = Assert.Single(response.Items);
        Assert.Equal("SKU-A", first.RetailerId);
        Assert.Equal("Alpha", first.Name);
        Assert.Equal(9.99m, first.Price);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupConnection(string workspaceId, WhatsAppConnection connection) =>
        _connectionRepo.Setup(r => r.GetByOwnerAndIdAsync(workspaceId, connection.Id))
            .ReturnsAsync(connection);

    private WhatsAppConnection Connection() =>
        new()
        {
            WorkspaceId = "ws",
            Name = "Test",
            VerifyTokenEncrypted = "enc:token",
        };

    private WhatsAppConnection ConnectedConnection()
    {
        WhatsAppConnection connection = Connection();
        connection.WhatsAppBusinessAccountId = "waba-123";
        connection.AccessTokenEncrypted = _protector.Protect("test-token");
        return connection;
    }

    private static MetaCatalogueProduct Product(string retailerId, string name, decimal? price = null) =>
        new()
        {
            MetaProductId = "mp-" + retailerId,
            RetailerId = retailerId,
            Name = name,
            Price = price,
        };

    // ── In-memory fakes ──────────────────────────────────────────────────────

    private sealed class FakeCatalogImportClient : ICatalogImportClient
    {
        public bool SyncCalled;
        public string? LastSyncWorkspaceId;
        public Guid LastSyncConnectionId;
        public IReadOnlyList<MetaCatalogueProduct>? LastSyncedProducts;
        public CatalogImportSyncResult SyncResult = new(0, 0, 0);

        public bool RetireCalled;
        public string? LastRetireWorkspaceId;
        public Guid LastRetireConnectionId;
        public IReadOnlyList<string>? LastRetirePresentSkus;
        public bool LastRetirePaginationCompleted;
        public int RetireResult;

        public bool GetProductsCalled;
        public string? LastGetProductsWorkspaceId;
        public Guid LastGetProductsConnectionId;
        public int LastGetProductsPage;
        public int LastGetProductsPageSize;
        public string? LastGetProductsCursor;
        public CatalogueItemsResponse ProductsResult = new([], 0, 1, 50);

        public Task<CatalogImportSyncResult> SyncProductsAsync(
            string workspaceId, Guid connectionId,
            IReadOnlyList<MetaCatalogueProduct> products, CancellationToken ct)
        {
            SyncCalled = true;
            LastSyncWorkspaceId = workspaceId;
            LastSyncConnectionId = connectionId;
            LastSyncedProducts = products;
            return Task.FromResult(SyncResult);
        }

        public Task<int> RetireMissingAsync(
            string workspaceId, Guid connectionId,
            IReadOnlyList<string> presentSkus, bool paginationCompleted,
            int validSkuCount, int failedProductCount, CancellationToken ct)
        {
            RetireCalled = true;
            LastRetireWorkspaceId = workspaceId;
            LastRetireConnectionId = connectionId;
            LastRetirePresentSkus = presentSkus;
            LastRetirePaginationCompleted = paginationCompleted;
            return Task.FromResult(RetireResult);
        }

        public Task<CatalogueItemsResponse> GetProductsAsync(
            string workspaceId, Guid connectionId, int page, int pageSize, string? cursor, CancellationToken ct)
        {
            GetProductsCalled = true;
            LastGetProductsWorkspaceId = workspaceId;
            LastGetProductsConnectionId = connectionId;
            LastGetProductsPage = page;
            LastGetProductsPageSize = pageSize;
            LastGetProductsCursor = cursor;
            return Task.FromResult(ProductsResult);
        }
    }

    private sealed class FakeMetaCatalogueClient : IMetaCatalogueClient
    {
        public string? CatalogueId = "cat-1";
        public List<MetaCatalogueProduct>? Products;
        public List<MetaCatalogueProductsPage>? Pages;

        public Task<string?> GetCatalogueIdAsync(string wabaId, string accessToken, CancellationToken ct) =>
            Task.FromResult(CatalogueId);

        public Task<MetaCatalogueProductsPage> GetApprovedProductsAsync(
            string catalogueId, string accessToken, string? cursor, CancellationToken ct)
        {
            if (Pages is not null)
            {
                int index = cursor is null ? 0 : int.Parse(cursor.Replace("cursor-", ""));
                return Task.FromResult(Pages[index]);
            }
            return Task.FromResult(new MetaCatalogueProductsPage(Products ?? [], null));
        }
    }
}
