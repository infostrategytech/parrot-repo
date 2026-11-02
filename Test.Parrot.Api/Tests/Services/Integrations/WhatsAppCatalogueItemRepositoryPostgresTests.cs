using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.SocialMediaIntegrations;
using static Test.Parrot.Api.Tests.Services.Integrations.ConnectionMessageReceiptPostgresTests;

namespace Test.Parrot.Api.Tests.Services.Integrations;

// Integration tests for WhatsAppCatalogueItemRepository.
// Requires a local Postgres instance: set PARROT_TEST_POSTGRES to a connection string whose
// user may create databases, e.g. Host=localhost;Port=15432;Username=postgres;Password=...
public class WhatsAppCatalogueItemRepositoryPostgresTests : IAsyncLifetime
{
    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private DbContextOptions<ApplicationDbContext>? _options;

    public async Task InitializeAsync()
    {
        string? server = Environment.GetEnvironmentVariable("PARROT_TEST_POSTGRES");
        if (server is null) return;
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(server) { Database = _database }.ConnectionString).Options;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    // ── Seeding helpers ──────────────────────────────────────────────────────

    private async Task<WhatsAppConnection> ConnectionAsync(string name = "conn")
    {
        await using ApplicationDbContext db = new(_options!);
        ApplicationUser user = new() { UserName = $"{name}@example.com", Email = $"{name}@example.com" };
        db.Users.Add(user);
        db.Workspaces.Add(new Workspace { Id = user.Id, OwnerId = user.Id });
        WhatsAppConnection connection = new()
        {
            WorkspaceId = user.Id,
            Name = name,
            VerifyTokenEncrypted = "enc:token",
            WhatsAppBusinessAccountId = "waba-123",
        };
        db.WhatsAppConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static WhatsAppCatalogueItem Item(Guid connectionId, string retailerId, string name = "Product",
        string catalogueId = "cat-1", DateTime? deletedAt = null) =>
        new()
        {
            ConnectionId = connectionId,
            MetaCatalogueId = catalogueId,
            MetaProductId = "meta-" + retailerId,
            RetailerId = retailerId,
            Name = name,
            LastSyncedAt = DateTime.UtcNow,
            DeletedAt = deletedAt,
        };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task GetActiveByConnection_ReturnsOnlyNonDeletedItemsForThatConnection()
    {
        WhatsAppConnection conn = await ConnectionAsync("conn-a");
        WhatsAppConnection other = await ConnectionAsync("conn-b");

        await using ApplicationDbContext db = new(_options!);
        WhatsAppCatalogueItemRepository repo = new(db);
        await repo.AddRangeAsync([
            Item(conn.Id, "SKU-1"),
            Item(conn.Id, "SKU-2"),
            Item(conn.Id, "SKU-3", deletedAt: DateTime.UtcNow),
            Item(other.Id, "SKU-1"), // different connection — must not appear
        ], default);

        IReadOnlyList<WhatsAppCatalogueItem> results = await repo.GetActiveByConnectionAsync(conn.Id, default);

        Assert.Equal(2, results.Count);
        Assert.All(results, i => Assert.Equal(conn.Id, i.ConnectionId));
        Assert.DoesNotContain(results, i => i.RetailerId == "SKU-3");
    }

    [PostgresFact]
    public async Task AddRangeAsync_PersistsAllItems()
    {
        WhatsAppConnection conn = await ConnectionAsync();

        await using ApplicationDbContext db = new(_options!);
        WhatsAppCatalogueItemRepository repo = new(db);
        await repo.AddRangeAsync([Item(conn.Id, "A"), Item(conn.Id, "B")], default);

        List<WhatsAppCatalogueItem> stored = await db.WhatsAppCatalogueItems
            .Where(i => i.ConnectionId == conn.Id).ToListAsync();
        Assert.Equal(2, stored.Count);
    }

    [PostgresFact]
    public async Task UpdateRangeAsync_ModifiesFieldsAndRestoresTombstone()
    {
        WhatsAppConnection conn = await ConnectionAsync();
        DateTime tombstoned = DateTime.UtcNow.AddHours(-1);

        await using ApplicationDbContext seed = new(_options!);
        seed.WhatsAppCatalogueItems.Add(Item(conn.Id, "SKU-1", "Old Name", deletedAt: tombstoned));
        await seed.SaveChangesAsync();

        await using ApplicationDbContext db = new(_options!);
        WhatsAppCatalogueItemRepository repo = new(db);
        WhatsAppCatalogueItem existing = (await repo.GetActiveByConnectionAsync(conn.Id, default))
            .SingleOrDefault() ?? await db.WhatsAppCatalogueItems.SingleAsync(i => i.RetailerId == "SKU-1");

        existing.Name = "New Name";
        existing.Price = 15.50m;
        existing.DeletedAt = null;
        existing.UpdatedAt = DateTime.UtcNow;

        await repo.UpdateRangeAsync([existing], default);

        WhatsAppCatalogueItem stored = await db.WhatsAppCatalogueItems.AsNoTracking()
            .SingleAsync(i => i.ConnectionId == conn.Id && i.RetailerId == "SKU-1");
        Assert.Equal("New Name", stored.Name);
        Assert.Equal(15.50m, stored.Price);
        Assert.Null(stored.DeletedAt);
    }

    [PostgresFact]
    public async Task TombstoneMissing_SoftDeletesItemsAbsentFromCurrentSync()
    {
        WhatsAppConnection conn = await ConnectionAsync();
        DateTime now = DateTime.UtcNow;

        await using ApplicationDbContext seed = new(_options!);
        seed.WhatsAppCatalogueItems.AddRange(
            Item(conn.Id, "keep-1"),
            Item(conn.Id, "keep-2"),
            Item(conn.Id, "remove-1"),
            Item(conn.Id, "remove-2"));
        await seed.SaveChangesAsync();

        await using ApplicationDbContext db = new(_options!);
        WhatsAppCatalogueItemRepository repo = new(db);
        int count = await repo.TombstoneMissingAsync(conn.Id, ["keep-1", "keep-2"], now, default);

        Assert.Equal(2, count);
        List<WhatsAppCatalogueItem> all = await db.WhatsAppCatalogueItems
            .AsNoTracking().Where(i => i.ConnectionId == conn.Id).ToListAsync();
        Assert.All(all.Where(i => i.RetailerId.StartsWith("remove")), i => Assert.NotNull(i.DeletedAt));
        Assert.All(all.Where(i => i.RetailerId.StartsWith("keep")), i => Assert.Null(i.DeletedAt));
    }

    [PostgresFact]
    public async Task TombstoneMissing_DoesNotTouchAnotherConnectionsItems()
    {
        WhatsAppConnection conn = await ConnectionAsync("conn-a");
        WhatsAppConnection other = await ConnectionAsync("conn-b");
        DateTime now = DateTime.UtcNow;

        await using ApplicationDbContext seed = new(_options!);
        seed.WhatsAppCatalogueItems.AddRange(
            Item(conn.Id, "SKU-1"),
            Item(other.Id, "SKU-1")); // same retailer id, different connection
        await seed.SaveChangesAsync();

        await using ApplicationDbContext db = new(_options!);
        await new WhatsAppCatalogueItemRepository(db)
            .TombstoneMissingAsync(conn.Id, [], now, default);

        WhatsAppCatalogueItem otherItem = await db.WhatsAppCatalogueItems
            .AsNoTracking().SingleAsync(i => i.ConnectionId == other.Id);
        Assert.Null(otherItem.DeletedAt);
    }

    [PostgresFact]
    public async Task GetPageByConnection_PaginatesAndReturnsTotal()
    {
        WhatsAppConnection conn = await ConnectionAsync();

        await using ApplicationDbContext seed = new(_options!);
        seed.WhatsAppCatalogueItems.AddRange(
            Item(conn.Id, "A", "Alpha"),
            Item(conn.Id, "B", "Beta"),
            Item(conn.Id, "C", "Gamma"),
            Item(conn.Id, "D", "Delta"),
            Item(conn.Id, "E", "Epsilon"),
            Item(conn.Id, "F", deletedAt: DateTime.UtcNow)); // excluded
        await seed.SaveChangesAsync();

        await using ApplicationDbContext db = new(_options!);
        WhatsAppCatalogueItemRepository repo = new(db);
        (IReadOnlyList<WhatsAppCatalogueItem> page1, int total) = await repo.GetPageByConnectionAsync(conn.Id, 1, 3, default);
        (IReadOnlyList<WhatsAppCatalogueItem> page2, _) = await repo.GetPageByConnectionAsync(conn.Id, 2, 3, default);

        Assert.Equal(5, total);
        Assert.Equal(3, page1.Count);
        Assert.Equal(2, page2.Count);
        // Ordered by name: Alpha, Beta, Delta, Epsilon, Gamma
        Assert.Equal("Alpha", page1[0].Name);
        Assert.Equal("Beta", page1[1].Name);
        Assert.Equal("Delta", page1[2].Name);
        Assert.Equal("Epsilon", page2[0].Name);
        Assert.Equal("Gamma", page2[1].Name);
    }
}
