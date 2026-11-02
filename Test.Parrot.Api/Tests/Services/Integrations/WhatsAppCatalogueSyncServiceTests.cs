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
    private readonly FakeItemRepository _itemRepo = new();
    private readonly FakeMetaCatalogueClient _catalogueClient = new();
    private readonly IWhatsAppTokenProtector _protector =
        new WhatsAppTokenProtector(new EphemeralDataProtectionProvider());

    private WhatsAppCatalogueSyncService CreateService() =>
        new(_connectionRepo.Object, _itemRepo, _catalogueClient, _protector,
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
        // Identical to the not-found case — no existence leak.
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

    // ── Sync behaviour tests ─────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_WhenNoCatalogueFound_ReturnsZeroResult()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogueClient.CatalogueId = null;

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.Null(result.CatalogueId);
    }

    [Fact]
    public async Task SyncAsync_WithNewProducts_InsertsAll()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogueClient.CatalogueId = "cat-1";
        _catalogueClient.Products = [Product("SKU-A", "Alpha"), Product("SKU-B", "Beta")];

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Deleted);
        Assert.Equal("cat-1", result.CatalogueId);
        Assert.Equal(2, _itemRepo.Inserted.Count);
        Assert.All(_itemRepo.Inserted, i => Assert.Equal(connection.Id, i.ConnectionId));
    }

    [Fact]
    public async Task SyncAsync_WithExistingProducts_UpdatesNameAndPrice()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogueClient.CatalogueId = "cat-1";
        _catalogueClient.Products = [Product("SKU-A", "Alpha Updated", price: 20.00m)];

        // Seed an existing item.
        WhatsAppCatalogueItem existing = ExistingItem(connection.Id, "SKU-A", "Alpha", 10.00m);
        _itemRepo.ExistingItems = [existing];

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal("Alpha Updated", _itemRepo.Updated.Single().Name);
        Assert.Equal(20.00m, _itemRepo.Updated.Single().Price);
    }

    [Fact]
    public async Task SyncAsync_WhenProductsRemovedFromMeta_TombstonesAbsentItems()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogueClient.CatalogueId = "cat-1";
        _catalogueClient.Products = [Product("SKU-A", "Alpha")]; // SKU-B is gone

        _itemRepo.ExistingItems = [
            ExistingItem(connection.Id, "SKU-A", "Alpha"),
            ExistingItem(connection.Id, "SKU-B", "Beta"), // should be tombstoned
        ];

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(["SKU-A"], _itemRepo.LastPresentRetailerIds);
    }

    [Fact]
    public async Task SyncAsync_WithEmptyCatalogue_TombstonesAllExisting()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogueClient.CatalogueId = "cat-1";
        _catalogueClient.Products = []; // catalogue has no approved products

        _itemRepo.ExistingItems = [ExistingItem(connection.Id, "SKU-A", "Alpha")];

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Deleted);
        Assert.Empty(_itemRepo.LastPresentRetailerIds ?? []);
    }

    [Fact]
    public async Task SyncAsync_WhenAllProductsMissingRetailerId_CountsFailedAndDoesNotTombstone()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogueClient.CatalogueId = "cat-1";
        _catalogueClient.Products = [Product("", "Bad product 1"), Product("   ", "Bad product 2")];

        _itemRepo.ExistingItems = [ExistingItem(connection.Id, "SKU-A", "Alpha")];

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(2, result.Failed);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        // Tombstoning is skipped when all products failed.
        Assert.Null(_itemRepo.LastPresentRetailerIds);
    }

    [Fact]
    public async Task SyncAsync_WithMultiplePages_FetchesAllProducts()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogueClient.CatalogueId = "cat-1";
        _catalogueClient.Pages = [
            new(Products: [Product("SKU-A", "Alpha"), Product("SKU-B", "Beta")], NextCursor: "cursor-1"),
            new(Products: [Product("SKU-C", "Gamma")], NextCursor: null),
        ];

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(3, result.Added);
        Assert.Equal(3, _itemRepo.Inserted.Count);
    }

    [Fact]
    public async Task SyncAsync_IsIdempotent_SecondRunUpdatesNotInserts()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _catalogueClient.CatalogueId = "cat-1";
        _catalogueClient.Products = [Product("SKU-A", "Alpha", 10.00m)];

        // Simulate first sync having already inserted SKU-A.
        _itemRepo.ExistingItems = [ExistingItem(connection.Id, "SKU-A", "Alpha", 10.00m)];

        CatalogueSyncResult result = await CreateService().SyncAsync("ws", connection.Id, default);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
    }

    // ── GetProductsAsync tests ───────────────────────────────────────────────

    [Fact]
    public async Task GetProductsAsync_WhenConnectionNotFound_ThrowsNotFoundException()
    {
        _connectionRepo.Setup(r => r.GetByOwnerAndIdAsync("ws", It.IsAny<Guid>()))
            .ReturnsAsync((WhatsAppConnection?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().GetProductsAsync("ws", Guid.NewGuid(), 1, 50, default));
    }

    [Fact]
    public async Task GetProductsAsync_CapsPageSizeAt100()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);
        _itemRepo.PageResult = ([], 0);

        CatalogueItemsResponse response =
            await CreateService().GetProductsAsync("ws", connection.Id, 1, 9999, default);

        Assert.Equal(100, response.PageSize);
    }

    [Fact]
    public async Task GetProductsAsync_MapsItemsToResponse()
    {
        WhatsAppConnection connection = ConnectedConnection();
        SetupConnection("ws", connection);

        WhatsAppCatalogueItem item = ExistingItem(connection.Id, "SKU-A", "Alpha", 9.99m);
        item.Currency = "USD";
        _itemRepo.PageResult = ([item], 1);

        CatalogueItemsResponse response =
            await CreateService().GetProductsAsync("ws", connection.Id, 1, 50, default);

        Assert.Equal(1, response.Total);
        CatalogueItemResponse first = Assert.Single(response.Items);
        Assert.Equal("SKU-A", first.RetailerId);
        Assert.Equal("Alpha", first.Name);
        Assert.Equal(9.99m, first.Price);
        Assert.Equal("USD", first.Currency);
        Assert.Equal("whatsapp", first.ChannelSource);
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

    private static WhatsAppCatalogueItem ExistingItem(
        Guid connectionId, string retailerId, string name, decimal? price = null) =>
        new()
        {
            ConnectionId = connectionId,
            MetaCatalogueId = "cat-1",
            MetaProductId = "mp-" + retailerId,
            RetailerId = retailerId,
            Name = name,
            Price = price,
            LastSyncedAt = DateTime.UtcNow.AddHours(-1),
        };

    // ── In-memory fakes ──────────────────────────────────────────────────────

    private sealed class FakeItemRepository : IWhatsAppCatalogueItemRepository
    {
        public List<WhatsAppCatalogueItem> ExistingItems = [];
        public List<WhatsAppCatalogueItem> Inserted = [];
        public List<WhatsAppCatalogueItem> Updated = [];
        public IReadOnlyCollection<string>? LastPresentRetailerIds;
        public (IReadOnlyList<WhatsAppCatalogueItem> Items, int Total) PageResult = ([], 0);

        public Task<IReadOnlyList<WhatsAppCatalogueItem>> GetActiveByConnectionAsync(
            Guid connectionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WhatsAppCatalogueItem>>(
                ExistingItems.Where(i => i.ConnectionId == connectionId && i.DeletedAt == null).ToList());

        public Task AddRangeAsync(IEnumerable<WhatsAppCatalogueItem> items, CancellationToken ct)
        {
            Inserted.AddRange(items);
            return Task.CompletedTask;
        }

        public Task UpdateRangeAsync(IEnumerable<WhatsAppCatalogueItem> items, CancellationToken ct)
        {
            Updated.AddRange(items);
            return Task.CompletedTask;
        }

        public Task<int> TombstoneMissingAsync(
            Guid connectionId, IReadOnlyCollection<string> presentRetailerIds,
            DateTime tombstonedAt, CancellationToken ct)
        {
            LastPresentRetailerIds = presentRetailerIds;
            int count = ExistingItems.Count(i =>
                i.ConnectionId == connectionId
                && i.DeletedAt == null
                && !presentRetailerIds.Contains(i.RetailerId));
            return Task.FromResult(count);
        }

        public Task<(IReadOnlyList<WhatsAppCatalogueItem> Items, int Total)> GetPageByConnectionAsync(
            Guid connectionId, int page, int pageSize, CancellationToken ct) =>
            Task.FromResult(PageResult);
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
                // Return pages in sequence based on cursor.
                int index = cursor is null ? 0 : int.Parse(cursor.Replace("cursor-", ""));
                return Task.FromResult(Pages[index]);
            }
            return Task.FromResult(new MetaCatalogueProductsPage(Products ?? [], null));
        }
    }
}
