using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.Contracts.BusinessSources;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.MessageBus;

namespace MessageProcessor;

/// <summary>
/// Polls the OutboxEvents table and publishes each pending event to the
/// <c>business.knowledge.sources</c> Kafka topic. Uses a lease-based claim so
/// the Kafka call happens OUTSIDE the claim transaction; a worker crash before
/// the row is marked published leaves the lease to expire naturally so another
/// worker (or the next pass) can reclaim it.
/// </summary>
public sealed class SourcesOutboxWorker : BackgroundService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IServiceScopeFactory _scopes;
    private readonly IMessageBus _bus;
    private readonly KafkaSettings _kafka;
    private readonly ILogger<SourcesOutboxWorker> _logger;

    private const int BatchSize = 50;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public SourcesOutboxWorker(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IServiceScopeFactory scopes,
        IMessageBus bus,
        IOptions<KafkaSettings> kafka,
        ILogger<SourcesOutboxWorker> logger)
    {
        _dbFactory = dbFactory;
        _scopes = scopes;
        _bus = bus;
        _kafka = kafka.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool kafkaConfigured = !string.IsNullOrWhiteSpace(_kafka.BootstrapServers)
            && _kafka.BootstrapServers != "localhost:9092";

        _logger.LogInformation(
            "SourcesOutboxWorker started. Topic={Topic} BatchSize={BatchSize} PollIntervalMs={PollIntervalMs} KafkaBootstrap={KafkaBootstrap} KafkaConfigured={KafkaConfigured}",
            _kafka.BusinessKnowledgeSources, BatchSize, (int)PollInterval.TotalMilliseconds,
            _kafka.BootstrapServers, kafkaConfigured);

        if (!kafkaConfigured)
            _logger.LogWarning(
                "SourcesOutboxWorker: Kafka:BootstrapServers is '{BootstrapServers}' — this is the default or empty. " +
                "Outbox events will not reach Omni AI until a production Kafka bootstrap address is configured. " +
                "Events accumulate in the OutboxEvents table and will be flushed once the setting is correct.",
                _kafka.BootstrapServers);

        try
        {
            await LogStartupStateAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only a diagnosis: the relay runs either way.
            _logger.LogWarning(ex, "business_knowledge.startup_check_failed Topic={Topic}", _kafka.BusinessKnowledgeSources);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            int published = 0;
            try
            {
                published = await RelayOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SourcesOutboxWorker relay pass failed; retrying after poll interval.");
            }

            if (published < BatchSize)
            {
                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<int> RelayOnceAsync(CancellationToken ct)
    {
        // ── 1. Claim a batch inside a short transaction ──────────────────────
        List<(Guid Id, string WorkspaceId, string Payload, DateTime CreatedAt, int Attempts)> claimed;
        {
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(ct);
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
                await db.Database.BeginTransactionAsync(ct);

            DateTime lockedUntil = DateTime.UtcNow.Add(LeaseDuration);

            List<OutboxEvent> batch = await db.OutboxEvents
                .FromSqlRaw("""
                    SELECT * FROM "OutboxEvents"
                    WHERE "PublishedAt" IS NULL
                      AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= NOW())
                      AND ("LockedUntil" IS NULL OR "LockedUntil" < NOW())
                    ORDER BY "CreatedAt"
                    LIMIT {0}
                    FOR UPDATE SKIP LOCKED
                    """, BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                await tx.RollbackAsync(ct);
                return 0;
            }

            foreach (OutboxEvent row in batch)
            {
                row.LockedUntil = lockedUntil;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            claimed = batch.Select(r => (r.Id, r.WorkspaceId, r.Payload, r.CreatedAt, r.AttemptCount)).ToList();
        }

        // ── 2. Publish to Kafka outside any DB transaction ───────────────────
        int published = 0;
        HashSet<string> replaced = [];
        foreach ((Guid id, string workspaceId, string payload, DateTime createdAt, int attempts) in claimed)
        {
            bool success = false;
            string? errorMessage = null;
            Exception? failure = null;
            try
            {
                BusinessSourcesUpdatedMessage? message = BusinessSourcesUpdatedMessage.FromStoredPayload(payload);
                if (message is null)
                {
                    // Queued before schema 2, a shape Omni can't read. The workspace as it is now
                    // replaces it: a snapshot is the whole picture, so nothing is lost.
                    if (replaced.Add(workspaceId)) await QueueCurrentSnapshotAsync(workspaceId, ct);
                    errorMessage = "Not sent: queued before schema 2 and replaced by a new snapshot.";
                    _logger.LogWarning("business_knowledge.snapshot_replaced EventId={EventId} WorkspaceId={WorkspaceId}", id, workspaceId);
                }
                else
                {
                    await _bus.PublishAsync(_kafka.BusinessKnowledgeSources, workspaceId, message, ct);
                    published++;
                    _logger.LogInformation(
                        "business_knowledge.snapshot_published EventId={EventId} CorrelationId={CorrelationId} WorkspaceId={WorkspaceId} " +
                        "SnapshotVersion={SnapshotVersion} Trigger={Trigger} Topic={Topic} Attempt={Attempt} WaitedMs={WaitedMs}",
                        id, message.CorrelationId, workspaceId, message.Data.SnapshotVersion, message.Data.Trigger,
                        _kafka.BusinessKnowledgeSources, attempts + 1, (long)(DateTime.UtcNow - createdAt).TotalMilliseconds);
                }

                success = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                failure = ex;
            }

            // ── 3. Mark each row in its own short transaction ────────────────
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(ct);
            OutboxEvent? row = await db.OutboxEvents.FindAsync([id], ct);
            if (row is null) continue;

            if (success)
            {
                row.PublishedAt = DateTime.UtcNow;
                row.LockedUntil = null;
                row.LastError = errorMessage;
            }
            else
            {
                row.AttemptCount += 1;
                row.LastError = errorMessage;
                row.LockedUntil = null;
                // Exponential backoff: 30s, 60s, 2m, 4m … capped at 1h.
                int backoffSeconds = Math.Min(3600, 30 * (int)Math.Pow(2, row.AttemptCount - 1));
                row.NextAttemptAt = DateTime.UtcNow.AddSeconds(backoffSeconds);
                // A missing topic or an unreachable broker reads here as Kafka's own reason ("Unknown topic or partition").
                _logger.LogError(failure,
                    "business_knowledge.snapshot_publish_failed EventId={EventId} WorkspaceId={WorkspaceId} Topic={Topic} KafkaBootstrap={KafkaBootstrap} " +
                    "Attempt={Attempt} NextAttemptAt={NextAttemptAt} Reason={Reason}",
                    id, workspaceId, _kafka.BusinessKnowledgeSources, _kafka.BootstrapServers, row.AttemptCount, row.NextAttemptAt, errorMessage);
            }

            await db.SaveChangesAsync(ct);
        }

        return published;
    }

    // Said once at start, for whoever wonders why Omni has heard nothing: does the topic exist
    // (topics are never created automatically), and is anything waiting or failing in the outbox.
    private async Task LogStartupStateAsync(CancellationToken ct)
    {
        string topic = _kafka.BusinessKnowledgeSources;
        if (_bus is KafkaMessageBus kafka)
        {
            try
            {
                int? partitions = await kafka.PartitionCountAsync(topic, TimeSpan.FromSeconds(10));
                if (partitions is null)
                    _logger.LogError(
                        "business_knowledge.topic_missing Topic={Topic} KafkaBootstrap={KafkaBootstrap}: no snapshot can be sent until it exists. " +
                        "Topics are not created automatically; add it to kafka-topic-init.",
                        topic, _kafka.BootstrapServers);
                else
                    _logger.LogInformation("business_knowledge.topic_ok Topic={Topic} Partitions={Partitions}", topic, partitions);
            }
            catch (KafkaException ex)
            {
                _logger.LogError(ex, "business_knowledge.topic_check_failed Topic={Topic} KafkaBootstrap={KafkaBootstrap} Reason={Reason}",
                    topic, _kafka.BootstrapServers, ex.Error.Reason);
            }
        }

        await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<OutboxEvent> waiting = db.OutboxEvents.Where(e => e.PublishedAt == null);
        int pending = await waiting.CountAsync(ct);
        int failing = await waiting.CountAsync(e => e.AttemptCount > 0, ct);
        DateTime? oldest = await waiting.MinAsync(e => (DateTime?)e.CreatedAt, ct);
        double? oldestMinutes = oldest is null ? null : Math.Round((DateTime.UtcNow - oldest.Value).TotalMinutes, 1);
        if (failing > 0)
            _logger.LogWarning(
                "business_knowledge.outbox_backlog Pending={Pending} Failing={Failing} OldestPendingMinutes={OldestPendingMinutes}: " +
                "each failing row keeps its last error in OutboxEvents.LastError",
                pending, failing, oldestMinutes);
        else
            _logger.LogInformation("business_knowledge.outbox_backlog Pending={Pending} Failing={Failing} OldestPendingMinutes={OldestPendingMinutes}",
                pending, failing, oldestMinutes);
    }

    private async Task QueueCurrentSnapshotAsync(string workspaceId, CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ISnapshotPublisher publisher = scope.ServiceProvider.GetRequiredService<ISnapshotPublisher>();
        await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
        await publisher.PublishAsync(workspaceId, SnapshotTrigger.Scheduled, ct);
        await tx.CommitAsync(ct);
    }
}
