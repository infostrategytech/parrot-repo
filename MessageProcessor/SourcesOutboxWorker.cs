using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Parrot.Application.Contracts.BusinessSources;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

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
    private readonly IMessageBus _bus;
    private readonly KafkaSettings _kafka;
    private readonly ILogger<SourcesOutboxWorker> _logger;

    private const int BatchSize = 50;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public SourcesOutboxWorker(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IMessageBus bus,
        IOptions<KafkaSettings> kafka,
        ILogger<SourcesOutboxWorker> logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _kafka = kafka.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SourcesOutboxWorker started. Topic={Topic} BatchSize={BatchSize} PollIntervalMs={PollIntervalMs}",
            _kafka.BusinessKnowledgeSources, BatchSize, (int)PollInterval.TotalMilliseconds);

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
        List<(Guid Id, string WorkspaceId, string Payload)> claimed;
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

            claimed = batch.Select(r => (r.Id, r.WorkspaceId, r.Payload)).ToList();
        }

        // ── 2. Publish to Kafka outside any DB transaction ───────────────────
        int published = 0;
        foreach ((Guid id, string workspaceId, string payload) in claimed)
        {
            bool success = false;
            string? errorMessage = null;
            try
            {
                // Deserialize the stored payload back to the typed message so that
                // KafkaMessageBus.PublishAsync serializes it correctly (not double-encoded).
                BusinessSourcesUpdatedMessage message =
                    JsonSerializer.Deserialize<BusinessSourcesUpdatedMessage>(payload, MessageJson.Options)
                    ?? throw new InvalidOperationException("Outbox payload deserialized as null.");
                await _bus.PublishAsync(_kafka.BusinessKnowledgeSources, workspaceId, message, ct);
                success = true;
                published++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                _logger.LogError(ex, "SourcesOutboxWorker publish failed. EventId={EventId} WorkspaceId={WorkspaceId} AttemptCount will increment.",
                    id, workspaceId);
            }

            // ── 3. Mark each row in its own short transaction ────────────────
            await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(ct);
            OutboxEvent? row = await db.OutboxEvents.FindAsync([id], ct);
            if (row is null) continue;

            if (success)
            {
                row.PublishedAt = DateTime.UtcNow;
                row.LockedUntil = null;
            }
            else
            {
                row.AttemptCount += 1;
                row.LastError = errorMessage;
                row.LockedUntil = null;
                // Exponential backoff: 30s, 60s, 2m, 4m … capped at 1h.
                int backoffSeconds = Math.Min(3600, 30 * (int)Math.Pow(2, row.AttemptCount - 1));
                row.NextAttemptAt = DateTime.UtcNow.AddSeconds(backoffSeconds);
            }

            await db.SaveChangesAsync(ct);
        }

        return published;
    }
}
