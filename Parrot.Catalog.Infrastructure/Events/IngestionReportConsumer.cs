using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Catalog.Domain.Entities;

namespace Parrot.Catalog.Infrastructure.Events;

/// <summary>
/// Records the AI team's report on one item message (catalog.ingestion-report) against the
/// item's row in item_ai_status, so the business can see whether the AI has caught up.
///
/// The AI team's contract: each report names the item message it answers in
/// <c>causation_id</c>. A report counts only when that is the latest message sent for the
/// item (the row's <c>sent_event_id</c>), so a late report about an earlier save is ignored
/// and a quick run of saves never shows "ready" too soon. Within one message, the AI may
/// report more than once (a failure, then a success after a retry), and a report can be
/// resent; the newest by <c>occurred_at</c> wins. A <c>superseded</c> report means the
/// message changed nothing, so it is ignored.
/// </summary>
public sealed class IngestionReportHandler
{
    public const string ConsumerName = "ai-status";

    private readonly RelayDbContext _db;
    private readonly CatalogMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly ILogger<IngestionReportHandler> _logger;

    public IngestionReportHandler(RelayDbContext db, CatalogMetrics metrics, TimeProvider clock, ILogger<IngestionReportHandler> logger)
    {
        _db = db;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
    }

    /// <returns>True when the report was recorded against the item.</returns>
    public async Task<bool> HandleAsync(string json, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || GuidOf(root, "event_id") is not { } reportId)
        {
            _logger.LogWarning("Ingestion report dropped: no event_id.");
            return false;
        }

        JsonElement payload = root.TryGetProperty("payload", out JsonElement p) && p.ValueKind == JsonValueKind.Object ? p : default;
        Guid? productId = GuidOf(payload, "product_id") ?? GuidOf(root, "aggregate_id");
        Guid? workspaceId = GuidOf(root, "workspace_id");
        if (productId is null || workspaceId is null)
        {
            _logger.LogWarning("Ingestion report dropped: no product or workspace. ReportId={ReportId}", reportId);
            return false;
        }

        if (await _db.ConsumerInbox.AnyAsync(c => c.Consumer == ConsumerName && c.EventId == reportId, ct))
        {
            _metrics.EventDeduplicated(ConsumerName);
            return false;
        }

        _db.ConsumerInbox.Add(new ConsumerInboxEntry { Consumer = ConsumerName, EventId = reportId, TenantId = workspaceId.Value, ProcessedAt = _clock.GetUtcNow().UtcDateTime });

        bool recorded = false;
        ItemAiStatus? row = await _db.ItemAiStatus.FirstOrDefaultAsync(s => s.ProductId == productId, ct);
        Guid? about = GuidOf(root, "causation_id") ?? GuidOf(root, "source_event_id") ?? GuidOf(payload, "source_event_id");
        DateTime reportedAt = DateOf(root, "occurred_at") ?? _clock.GetUtcNow().UtcDateTime;
        string? status = StringOf(payload, "status");
        if (row is null || row.TenantId != workspaceId)
        {
            // Never sent by this catalog, or named under another workspace: nothing to update.
            _logger.LogWarning("Ingestion report for an unknown item. ReportId={ReportId} ProductId={ProductId} WorkspaceId={WorkspaceId}", reportId, productId, workspaceId);
        }
        else if (about is not null && about != row.SentEventId)
        {
            _logger.LogInformation("Ingestion report is about an earlier save; ignored. ReportId={ReportId} ProductId={ProductId}", reportId, productId);
        }
        else if (Is(status, "superseded"))
        {
            _logger.LogInformation("Ingestion report says the save changed nothing (superseded); ignored. ReportId={ReportId} ProductId={ProductId}", reportId, productId);
        }
        else if (row.ReportedEventId == row.SentEventId && row.ReportedAt > reportedAt)
        {
            _logger.LogInformation("Ingestion report is older than one already recorded for this save; ignored. ReportId={ReportId} ProductId={ProductId}", reportId, productId);
        }
        else
        {
            Apply(row, reportId, reportedAt, root, payload);
            recorded = true;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another consumer instance recorded the same report first.
            _metrics.EventDeduplicated(ConsumerName);
            return false;
        }

        _metrics.EventConsumed(ConsumerName, StringOf(root, "type") ?? "unknown");
        return recorded;
    }

    private static void Apply(ItemAiStatus row, Guid reportId, DateTime reportedAt, JsonElement root, JsonElement payload)
    {
        row.ReportedEventId = row.SentEventId;
        row.ReportId = reportId;
        row.ReportedAt = reportedAt;
        row.ReportType = Clip(StringOf(root, "type"), 64);
        row.Status = Clip(StringOf(payload, "status"), 64);
        row.Searchable = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("searchable", out JsonElement searchable)
            && searchable.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? searchable.GetBoolean()
                : null;

        JsonElement text = Object(payload, "text");
        row.TextStatus = Clip(StringOf(text, "status"), 64);
        row.TextReasonCode = Clip(StringOf(text, "reason_code"), 64);
        row.TextReason = Clip(StringOf(text, "reason"), 1000);

        JsonElement photos = Object(payload, "photos");
        row.PhotosStatus = Clip(StringOf(photos, "status"), 64);
        row.Photos = null;
        row.PhotosRejected = 0;
        row.PhotosFailed = 0;
        if (photos.ValueKind == JsonValueKind.Object && photos.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            row.Photos = items.GetRawText();
            foreach (JsonElement item in items.EnumerateArray())
            {
                string? photoStatus = StringOf(item, "status");
                if (Is(photoStatus, "rejected"))
                {
                    row.PhotosRejected++;
                }
                else if (Is(photoStatus, "failed"))
                {
                    row.PhotosFailed++;
                }
            }
        }
    }

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string? Clip(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    private static JsonElement Object(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string? StringOf(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Guid? GuidOf(JsonElement element, string name)
        => Guid.TryParse(StringOf(element, name), out Guid id) ? id : null;

    private static DateTime? DateOf(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out DateTime at)
            ? at.ToUniversalTime()
            : null;
}

/// <summary>Kafka consumer loop feeding <see cref="IngestionReportHandler"/>.</summary>
public sealed class IngestionReportConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CatalogRelayOptions _options;
    private readonly ILogger<IngestionReportConsumer> _logger;

    public IngestionReportConsumer(IServiceScopeFactory scopes, IOptions<CatalogOptions> options, ILogger<IngestionReportConsumer> logger)
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
            GroupId = _options.IngestionReportGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };
        using IConsumer<string, string> consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(_options.IngestionReportTopic);
        _logger.LogInformation("AI status consumer subscribed. Topic={Topic} GroupId={GroupId}", _options.IngestionReportTopic, _options.IngestionReportGroupId);

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
                    using IServiceScope scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IngestionReportHandler>().HandleAsync(result.Message.Value, ct);
                    consumer.Commit(result);
                }
                catch (JsonException ex)
                {
                    // Not JSON: retrying won't help, so skip it rather than block the partition.
                    _logger.LogWarning(ex, "Ingestion report is not valid JSON; skipped. Offset={Offset}", result.Offset.Value);
                    consumer.Commit(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Recording an ingestion report failed; it will be redelivered. Offset={Offset}", result.Offset.Value);
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
