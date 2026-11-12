using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.Contracts.BusinessSources;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.BusinessSources;

/// <summary>
/// Runs inside the caller's open transaction. Locks WorkspaceSourceState, builds
/// the full active-source snapshot, increments the version, writes the OutboxEvent,
/// and calls SaveChanges. The caller commits or rolls back the outer transaction.
/// </summary>
public sealed class SnapshotPublisher : ISnapshotPublisher
{
    private readonly ApplicationDbContext _db;
    private readonly KafkaSettings _kafka;
    private readonly IAppLogger<SnapshotPublisher> _logger;

    public SnapshotPublisher(ApplicationDbContext db, IOptions<KafkaSettings> kafka, IAppLogger<SnapshotPublisher> logger)
    {
        _db = db;
        _kafka = kafka.Value;
        _logger = logger;
    }

    public async Task PublishAsync(string workspaceId, CancellationToken ct = default)
    {
        // Ensure the state row exists (idempotent; no-op if it already does).
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkspaceSourceStates" ("WorkspaceId", "SnapshotVersion", "UpdatedAt")
            VALUES ({workspaceId}, 0, NOW())
            ON CONFLICT ("WorkspaceId") DO NOTHING
            """, ct);

        // Lock the row for the duration of this transaction so concurrent callers
        // cannot read a stale version and produce duplicate snapshot version numbers.
        WorkspaceSourceState state = await _db.WorkspaceSourceStates
            .FromSqlInterpolated($"""
                SELECT * FROM "WorkspaceSourceStates"
                WHERE "WorkspaceId" = {workspaceId}
                FOR UPDATE
                """)
            .SingleAsync(ct);

        IReadOnlyList<BusinessSource> sources = await _db.BusinessSources
            .Where(s => s.WorkspaceId == workspaceId && s.IsActive)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        string? workspaceName = await _db.Workspaces
            .Where(w => w.Id == workspaceId)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(workspaceName))
            throw new InvalidOperationException($"Cannot publish business-knowledge snapshot: workspace {workspaceId} has no name set.");

        // Load provider-specific data needed for the snapshot (WABA IDs for WhatsApp sources).
        Dictionary<Guid, string?> connectionWabaIds = await LoadWabaIdsAsync(sources, ct);

        state.SnapshotVersion += 1;

        string payload = BuildPayload(state.SnapshotVersion, workspaceId, workspaceName!, sources, connectionWabaIds);

        _logger.LogInformation(
            "business_knowledge.snapshot_payload WorkspaceId={WorkspaceId} SnapshotVersion={SnapshotVersion} SourceCount={SourceCount} Payload={Payload}",
            workspaceId, state.SnapshotVersion, sources.Count, payload);

        OutboxEvent outbox = new OutboxEvent
        {
            WorkspaceId = workspaceId,
            Payload = payload,
        };

        _db.OutboxEvents.Add(outbox);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("business_knowledge.snapshot_queued SnapshotVersion={SnapshotVersion} WorkspaceId={WorkspaceId} Sources={SourceCount} OutboxEventId={OutboxEventId}",
            state.SnapshotVersion, workspaceId, sources.Count, outbox.Id);
    }

    private async Task<Dictionary<Guid, string?>> LoadWabaIdsAsync(IReadOnlyList<BusinessSource> sources, CancellationToken ct)
    {
        List<Guid> whatsAppConnectionIds = sources
            .Where(s => s.Channel == Channel.WhatsApp)
            .Select(s => s.AuthorizationConnectionId)
            .Distinct()
            .ToList();

        if (whatsAppConnectionIds.Count == 0)
            return new Dictionary<Guid, string?>();

        // Load only the WABA ID — no access tokens, no sensitive fields.
        return await _db.WhatsAppConnections
            .AsNoTracking()
            .Where(c => whatsAppConnectionIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.WhatsAppBusinessAccountId, ct);
    }

    private static string BuildPayload(
        long snapshotVersion,
        string workspaceId,
        string workspaceName,
        IReadOnlyList<BusinessSource> sources,
        IReadOnlyDictionary<Guid, string?> connectionWabaIds)
    {
        BusinessSourcesUpdatedMessage message = new BusinessSourcesUpdatedMessage
        {
            EventId = Guid.NewGuid().ToString(),
            WorkspaceId = workspaceId,
            SnapshotVersion = snapshotVersion,
            OccurredAt = DateTimeOffset.UtcNow,
            Data = new BusinessData { Name = workspaceName },
            Sources = sources.Select(s => ToEntry(s, connectionWabaIds)).ToList(),
        };

        // Use MessageJson.Options so the payload is consistent with what KafkaMessageBus
        // would produce if the typed message were published directly. The [JsonPropertyName]
        // attributes ensure snake_case regardless of the naming policy.
        return JsonSerializer.Serialize(message, MessageJson.Options);
    }

    private static SourceEntry ToEntry(BusinessSource source, IReadOnlyDictionary<Guid, string?> connectionWabaIds)
    {
        string channelKey = source.Channel switch
        {
            Channel.WhatsApp => "whatsapp",
            Channel.Instagram => "instagram",
            Channel.Facebook => "facebook",
            _ => source.Channel.ToString().ToLowerInvariant(),
        };

        string kindKey = source.Kind switch
        {
            BusinessSourceKind.Profile => "profile",
            BusinessSourceKind.Catalog => "catalog",
            _ => source.Kind.ToString().ToLowerInvariant(),
        };

        string sourceId = source.Kind == BusinessSourceKind.Catalog
            ? $"{channelKey}_catalog:{source.ExternalId}"
            : $"{channelKey}:{source.ExternalId}";

        JsonElement? providerData = source.Channel switch
        {
            Channel.WhatsApp => BuildWhatsAppProviderData(source, connectionWabaIds),
            Channel.Instagram => BuildInstagramProviderData(source),
            Channel.Facebook => BuildFacebookProviderData(source),
            _ => null,
        };

        return new SourceEntry
        {
            SourceId = sourceId,
            ExternalId = source.ExternalId,
            ConnectionId = source.AuthorizationConnectionId.ToString(),
            Channel = channelKey,
            Kind = kindKey,
            DisplayName = source.DisplayName,
            Handle = source.Handle,
            Url = source.Url,
            Text = source.Text,
            CapturedAt = source.CapturedAt.HasValue ? new DateTimeOffset(source.CapturedAt.Value, TimeSpan.Zero) : null,
            ProviderData = providerData,
        };
    }

    private static JsonElement BuildWhatsAppProviderData(
        BusinessSource source, IReadOnlyDictionary<Guid, string?> connectionWabaIds)
    {
        connectionWabaIds.TryGetValue(source.AuthorizationConnectionId, out string? wabaId);

        Dictionary<string, string?> data = source.Kind == BusinessSourceKind.Catalog
            ? new Dictionary<string, string?> { ["waba_id"] = wabaId, ["catalog_id"] = source.ExternalId }
            : new Dictionary<string, string?> { ["waba_id"] = wabaId, ["phone_number_id"] = source.ExternalId };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildInstagramProviderData(BusinessSource source) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string?> { ["account_id"] = source.ExternalId });

    private static JsonElement BuildFacebookProviderData(BusinessSource source) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string?> { ["page_id"] = source.ExternalId });
}
