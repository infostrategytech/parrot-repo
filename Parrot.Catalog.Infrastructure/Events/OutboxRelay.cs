using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Catalog.Application.Events;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Infrastructure.Data;

namespace Parrot.Catalog.Infrastructure.Events;

/// <summary>
/// One pass over the outbox: lock a batch of unpublished rows, publish each, mark it.
/// Separated from the hosted service so tests can drive it synchronously (ADR-005).
/// </summary>
public sealed class OutboxRelay
{
    private readonly RelayDbContext _db;
    private readonly IEventBusPublisher _publisher;
    private readonly CatalogMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxRelay> _logger;

    public OutboxRelay(RelayDbContext db, IEventBusPublisher publisher, CatalogMetrics metrics, TimeProvider clock, ILogger<OutboxRelay> logger)
    {
        _db = db;
        _publisher = publisher;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
    }

    /// <returns>How many rows were published in this pass.</returns>
    public async Task<int> RelayOnceAsync(int batchSize, CancellationToken ct)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
        List<OutboxMessage> batch = await _db.Outbox
            .FromSqlRaw("SELECT * FROM catalog.outbox WHERE published_at IS NULL ORDER BY created_at LIMIT {0} FOR UPDATE SKIP LOCKED", batchSize)
            .ToListAsync(ct);

        int published = 0;
        int failed = 0;
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        foreach (OutboxMessage row in batch)
        {
            try
            {
                using JsonDocument payload = JsonDocument.Parse(row.Payload);
                EventEnvelope envelope = new EventEnvelope(row.Id, row.TenantId, row.AggregateType, row.AggregateId, row.EventType, row.SchemaVersion, row.CreatedAt, payload.RootElement.Clone());
                await _publisher.PublishAsync(envelope, ct);
                row.PublishedAt = now;
                row.Attempts += 1;
                row.LastError = null;
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.Attempts += 1;
                row.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                failed++;
                _logger.LogError(ex, "Outbox publish failed. EventId={EventId} Type={Type} Attempts={Attempts}", row.Id, row.EventType, row.Attempts);
            }
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (published > 0)
        {
            _metrics.OutboxPublished(published);
        }

        if (failed > 0)
        {
            _metrics.OutboxFailed(failed);
        }

        await ObserveLagAsync(ct);
        return published;
    }

    public async Task<int> SweepAsync(int retentionDays, CancellationToken ct)
    {
        DateTime cutoff = _clock.GetUtcNow().UtcDateTime.AddDays(-retentionDays);
        return await _db.Outbox.Where(o => o.PublishedAt != null && o.PublishedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private async Task ObserveLagAsync(CancellationToken ct)
    {
        DateTime? oldest = await _db.Outbox.Where(o => o.PublishedAt == null).OrderBy(o => o.CreatedAt).Select(o => (DateTime?)o.CreatedAt).FirstOrDefaultAsync(ct);
        long pending = oldest is null ? 0 : await _db.Outbox.CountAsync(o => o.PublishedAt == null, ct);
        double lag = oldest is null ? 0 : (_clock.GetUtcNow().UtcDateTime - oldest.Value).TotalSeconds;
        _metrics.OutboxObserved(lag, pending);
    }
}

/// <summary>Polls the outbox on an interval. Runs in Parrot.Catalog.Relay, or in-process in development.</summary>
public sealed class OutboxRelayService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CatalogRelayOptions _options;
    private readonly ILogger<OutboxRelayService> _logger;

    public OutboxRelayService(IServiceScopeFactory scopes, IOptions<CatalogOptions> options, ILogger<OutboxRelayService> logger)
    {
        _scopes = scopes;
        _options = options.Value.Relay;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox relay started. Topic={Topic} PollIntervalMs={Interval} BatchSize={Batch}", _options.Topic, _options.PollIntervalMs, _options.BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            int published = 0;
            try
            {
                using IServiceScope scope = _scopes.CreateScope();
                OutboxRelay relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
                published = await relay.RelayOnceAsync(_options.BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox relay pass failed; retrying after the poll interval.");
            }

            if (published < _options.BatchSize)
            {
                try
                {
                    await Task.Delay(_options.PollIntervalMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>The relay's own context: outbox and consumer_inbox only, connected as catalog_outbox_relay.</summary>
public class RelayDbContext : DbContext
{
    public RelayDbContext(DbContextOptions<RelayDbContext> options)
        : base(options)
    {
    }

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<ConsumerInboxEntry> ConsumerInbox => Set<ConsumerInboxEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("catalog");
        builder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).HasColumnName("id");
            e.Property(o => o.TenantId).HasColumnName("tenant_id");
            e.Property(o => o.AggregateType).HasColumnName("aggregate_type");
            e.Property(o => o.AggregateId).HasColumnName("aggregate_id");
            e.Property(o => o.EventType).HasColumnName("event_type");
            e.Property(o => o.SchemaVersion).HasColumnName("schema_version");
            e.Property(o => o.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(o => o.CreatedAt).HasColumnName("created_at");
            e.Property(o => o.PublishedAt).HasColumnName("published_at");
            e.Property(o => o.Attempts).HasColumnName("attempts");
            e.Property(o => o.LastError).HasColumnName("last_error");
        });
        builder.Entity<ConsumerInboxEntry>(e =>
        {
            e.ToTable("consumer_inbox");
            e.HasKey(c => new { c.Consumer, c.EventId });
            e.Property(c => c.Consumer).HasColumnName("consumer");
            e.Property(c => c.EventId).HasColumnName("event_id");
            e.Property(c => c.TenantId).HasColumnName("tenant_id");
            e.Property(c => c.ProcessedAt).HasColumnName("processed_at");
        });
    }
}
