using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Catalog.Application.Caching;
using Parrot.Catalog.Application.Events;
using Parrot.Catalog.Domain.Entities;

namespace Parrot.Catalog.Infrastructure.Events;

/// <summary>
/// Handles one envelope for the cache invalidator: record it in consumer_inbox, and if
/// it is new, bump the tenant's cache generation. Redelivery is a no-op (ADR-005).
/// </summary>
public sealed class CacheInvalidationHandler
{
    public const string ConsumerName = "cache-invalidator";

    private readonly RelayDbContext _db;
    private readonly ICatalogReadCache _cache;
    private readonly CatalogMetrics _metrics;
    private readonly TimeProvider _clock;

    public CacheInvalidationHandler(RelayDbContext db, ICatalogReadCache cache, CatalogMetrics metrics, TimeProvider clock)
    {
        _db = db;
        _cache = cache;
        _metrics = metrics;
        _clock = clock;
    }

    /// <returns>True when the event was handled for the first time.</returns>
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken ct)
    {
        bool seen = await _db.ConsumerInbox.AnyAsync(c => c.Consumer == ConsumerName && c.EventId == envelope.EventId, ct);
        if (seen)
        {
            _metrics.EventDeduplicated(ConsumerName);
            return false;
        }

        _db.ConsumerInbox.Add(new ConsumerInboxEntry { Consumer = ConsumerName, EventId = envelope.EventId, TenantId = envelope.TenantId, ProcessedAt = _clock.GetUtcNow().UtcDateTime });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent consumer instance recorded it first.
            _metrics.EventDeduplicated(ConsumerName);
            return false;
        }

        if (CatalogEventTypes.InvalidatesReadCache.Contains(envelope.Type))
        {
            await _cache.BumpGenerationAsync(envelope.TenantId, ct);
        }

        _metrics.EventConsumed(ConsumerName, envelope.Type);
        return true;
    }
}

/// <summary>Kafka consumer loop feeding <see cref="CacheInvalidationHandler"/>.</summary>
public sealed class CacheInvalidationConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CatalogRelayOptions _options;
    private readonly ILogger<CacheInvalidationConsumer> _logger;

    public CacheInvalidationConsumer(IServiceScopeFactory scopes, IOptions<CatalogOptions> options, ILogger<CacheInvalidationConsumer> logger)
    {
        _scopes = scopes;
        _options = options.Value.Relay;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => ConsumeLoopAsync(stoppingToken), CancellationToken.None);

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        ConsumerConfig config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };
        using IConsumer<string, string> consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(_options.Topic);
        _logger.LogInformation("Cache invalidation consumer subscribed. Topic={Topic} GroupId={GroupId}", _options.Topic, _options.ConsumerGroupId);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(ct);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Kafka consume error. Reason={Reason}", ex.Error.Reason);
                    continue;
                }

                try
                {
                    EventEnvelope? envelope = JsonSerializer.Deserialize<EventEnvelope>(result.Message.Value, EventEnvelope.WireOptions);
                    if (envelope is not null)
                    {
                        using IServiceScope scope = _scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<CacheInvalidationHandler>().HandleAsync(envelope, ct);
                    }

                    consumer.Commit(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Cache invalidation failed; message will be redelivered. Offset={Offset}", result.Offset.Value);
                    consumer.Seek(result.TopicPartitionOffset);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            consumer.Close();
        }
    }
}
