using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Parrot.Catalog.Application.Caching;
using Parrot.Catalog.Application.Events;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Infrastructure;
using Parrot.Catalog.Infrastructure.Caching;
using Parrot.Catalog.Infrastructure.Events;
using Test.Parrot.Catalog.Fixtures;

namespace Test.Parrot.Catalog.Integration;

[Collection(CatalogApiCollection.Name)]
public class OutboxRelayTests
{
    private readonly CatalogApiFixture _fx;

    public OutboxRelayTests(CatalogApiFixture fx)
    {
        _fx = fx;
    }

    [PostgresFact]
    public async Task Relay_publishes_every_pending_row_once_with_the_envelope_and_consumers_dedupe()
    {
        Guid tenant = Guid.NewGuid();
        await _fx.SeedAsync(tenant, "relay");

        RecordingPublisher publisher = new RecordingPublisher();
        CatalogMetrics metrics = new CatalogMetrics();
        DbContextOptions<RelayDbContext> options = new DbContextOptionsBuilder<RelayDbContext>().UseNpgsql(_fx.Database.Relay).Options;

        int total = 0;
        await using (RelayDbContext db = new RelayDbContext(options))
        {
            OutboxRelay relay = new OutboxRelay(db, publisher, metrics, TimeProvider.System, NullLogger<OutboxRelay>.Instance);
            int published;
            do
            {
                published = await relay.RelayOnceAsync(50, CancellationToken.None);
                total += published;
            }
            while (published > 0);
        }

        Assert.True(total > 0);
        Assert.Equal(total, publisher.Envelopes.Count);
        Assert.Equal(publisher.Envelopes.Count, publisher.Envelopes.Select(e => e.EventId).Distinct().Count());
        List<EventEnvelope> mine = publisher.Envelopes.Where(e => e.TenantId == tenant).ToList();
        Assert.Contains(mine, e => e.Type == CatalogEventTypes.ProductPublished && e.AggregateType == AggregateTypes.Product);
        Assert.Contains(mine, e => e.Type == CatalogEventTypes.PriceChanged);
        Assert.Contains(mine, e => e.Type == CatalogEventTypes.TypeVersionCreated);
        Assert.All(mine, e => Assert.Equal(1, e.SchemaVersion));

        // Every envelope says who caused it (the seed runs as "seed"), and each seeded
        // product also gets one whole-item event for catalog.items.
        Assert.All(mine, e => Assert.Equal("seed", e.UserId));
        Assert.Contains(mine, e => e.Type == CatalogEventTypes.ItemChanged);
        Assert.All(mine, e => Assert.True(e.Payload.ValueKind == System.Text.Json.JsonValueKind.Object));

        await using (RelayDbContext db = new RelayDbContext(options))
        {
            OutboxRelay relay = new OutboxRelay(db, publisher, metrics, TimeProvider.System, NullLogger<OutboxRelay>.Instance);
            Assert.Equal(0, await relay.RelayOnceAsync(50, CancellationToken.None));
        }

        // The cache invalidator: first delivery bumps the generation, redelivery is a no-op.
        IOptions<CatalogOptions> catalogOptions = Options.Create(new CatalogOptions());
        MemoryCatalogReadCache cache = new MemoryCatalogReadCache(new MemoryCache(new MemoryCacheOptions()), catalogOptions);
        await cache.SetAsync(tenant, "r", "h", new CachedResponse([1], "\"e\"", DateTime.UtcNow), CancellationToken.None);
        Assert.NotNull(await cache.GetAsync(tenant, "r", "h", CancellationToken.None));

        EventEnvelope publishedEvent = mine.First(e => e.Type == CatalogEventTypes.ProductPublished);
        await using (RelayDbContext db = new RelayDbContext(options))
        {
            CacheInvalidationHandler handler = new CacheInvalidationHandler(db, cache, metrics, TimeProvider.System);
            Assert.True(await handler.HandleAsync(publishedEvent, CancellationToken.None));
        }

        Assert.Null(await cache.GetAsync(tenant, "r", "h", CancellationToken.None));
        await cache.SetAsync(tenant, "r", "h", new CachedResponse([2], "\"e2\"", DateTime.UtcNow), CancellationToken.None);

        await using (RelayDbContext db = new RelayDbContext(options))
        {
            CacheInvalidationHandler handler = new CacheInvalidationHandler(db, cache, metrics, TimeProvider.System);
            Assert.False(await handler.HandleAsync(publishedEvent, CancellationToken.None));
        }

        Assert.NotNull(await cache.GetAsync(tenant, "r", "h", CancellationToken.None));
    }

    [PostgresFact]
    public async Task A_failing_publisher_leaves_rows_pending_with_the_error_recorded()
    {
        Guid tenant = Guid.NewGuid();
        using (HttpClient client = _fx.Client(tenant, CatalogScopes.Admin))
        {
            HttpResponseMessage response = await client.PostAsync("/api/catalog/admin/types", CatalogApiFixture.Raw("""{"key":"t_fail","name":"T","kind":"good"}"""));
            response.EnsureSuccessStatusCode();
        }

        DbContextOptions<RelayDbContext> options = new DbContextOptionsBuilder<RelayDbContext>().UseNpgsql(_fx.Database.Relay).Options;
        await using RelayDbContext db = new RelayDbContext(options);
        OutboxRelay relay = new OutboxRelay(db, new FailingPublisher(), new CatalogMetrics(), TimeProvider.System, NullLogger<OutboxRelay>.Instance);
        Assert.Equal(0, await relay.RelayOnceAsync(50, CancellationToken.None));

        long pending = await _fx.Database.ScalarAsync<long>(_fx.Database.Relay, null, $"SELECT count(*) FROM catalog.outbox WHERE tenant_id = '{tenant}' AND published_at IS NULL AND attempts = 1 AND last_error LIKE 'broker down%'");
        Assert.Equal(1, pending);
    }

    private sealed class RecordingPublisher : IEventBusPublisher
    {
        public List<EventEnvelope> Envelopes { get; } = new List<EventEnvelope>();

        public Task PublishAsync(EventEnvelope envelope, CancellationToken ct)
        {
            Envelopes.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPublisher : IEventBusPublisher
    {
        public Task PublishAsync(EventEnvelope envelope, CancellationToken ct) => throw new InvalidOperationException("broker down");
    }
}
