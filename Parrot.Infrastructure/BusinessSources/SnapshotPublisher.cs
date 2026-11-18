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

    public async Task PublishAsync(string workspaceId, SnapshotTrigger trigger, CancellationToken ct = default)
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

        Workspace? workspace = await _db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workspaceId, ct);

        if (workspace is null || string.IsNullOrWhiteSpace(workspace.Name))
            throw new InvalidOperationException($"Cannot publish business-knowledge snapshot: workspace {workspaceId} is missing or has no name set.");

        // Load provider-specific data needed for the snapshot (WABA IDs for WhatsApp sources).
        Dictionary<Guid, string?> connectionWabaIds = await LoadWabaIdsAsync(sources, ct);

        state.SnapshotVersion += 1;

        // The outbox row's id is the event id, so a retried row is the same event to Omni.
        Guid eventId = Guid.NewGuid();
        BusinessSourcesUpdatedMessage message = BuildMessage(eventId, state.SnapshotVersion, trigger, workspace, workspace.Name, sources, connectionWabaIds);
        // Use MessageJson.Options so the payload is consistent with what KafkaMessageBus
        // would produce if the typed message were published directly.
        string payload = JsonSerializer.Serialize(message, MessageJson.Options);

        _logger.LogInformation(
            "business_knowledge.snapshot_payload EventId={EventId} CorrelationId={CorrelationId} WorkspaceId={WorkspaceId} TenantId={TenantId} " +
            "SnapshotVersion={SnapshotVersion} Trigger={Trigger} SourceCount={SourceCount} Payload={Payload}",
            eventId, message.CorrelationId, workspaceId, workspace.OwnerId, state.SnapshotVersion, trigger, sources.Count,
            JsonSerializer.Serialize(ForLog(message), MessageJson.Options));

        OutboxEvent outbox = new OutboxEvent
        {
            Id = eventId,
            WorkspaceId = workspaceId,
            Payload = payload,
        };

        _db.OutboxEvents.Add(outbox);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "business_knowledge.snapshot_queued EventId={EventId} CorrelationId={CorrelationId} WorkspaceId={WorkspaceId} SnapshotVersion={SnapshotVersion} " +
            "Trigger={Trigger} Sources={SourceCount}",
            outbox.Id, message.CorrelationId, workspaceId, state.SnapshotVersion, trigger, sources.Count);
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

    private static BusinessSourcesUpdatedMessage BuildMessage(
        Guid eventId,
        long snapshotVersion,
        SnapshotTrigger trigger,
        Workspace workspace,
        string businessName,
        IReadOnlyList<BusinessSource> sources,
        IReadOnlyDictionary<Guid, string?> connectionWabaIds)
    {
        BusinessSourcesUpdatedMessage message = new BusinessSourcesUpdatedMessage
        {
            EventId = eventId.ToString(),
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = InboundMessagePublisher.CurrentCorrelationId(),
            Data = new BusinessSourcesSnapshot
            {
                WorkspaceId = workspace.Id,
                TenantId = workspace.OwnerId,
                Trigger = trigger,
                SnapshotVersion = snapshotVersion,
                Business = new BusinessData { Name = businessName },
                Sources = sources.Select(s => ToEntry(s, connectionWabaIds)).ToList(),
            },
        };

        return message;
    }

    // What the log shows: the same message with the business's phone, email and address masked.
    private static BusinessSourcesUpdatedMessage ForLog(BusinessSourcesUpdatedMessage message) => message with
    {
        Data = message.Data with
        {
            Sources =
            [
                .. message.Data.Sources.Select(source => source with
                {
                    Handle = source.Channel == "whatsapp" ? LogMask.Phone(source.Handle) : source.Handle,
                    Text = LogMask.ProfileJson(source.Text),
                }),
            ],
        },
    };

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
            ? new Dictionary<string, string?> { ["wabaId"] = wabaId, ["catalogId"] = source.ExternalId }
            : new Dictionary<string, string?> { ["wabaId"] = wabaId, ["phoneNumberId"] = source.ExternalId };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildInstagramProviderData(BusinessSource source) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string?> { ["accountId"] = source.ExternalId });

    private static JsonElement BuildFacebookProviderData(BusinessSource source) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string?> { ["pageId"] = source.ExternalId });
}
