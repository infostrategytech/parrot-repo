using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Catalog.Application.Events;

public static class CatalogEventTypes
{
    public const string ProductCreated = "ProductCreated";
    public const string ProductUpdated = "ProductUpdated";
    public const string ProductPublished = "ProductPublished";
    public const string ProductRetired = "ProductRetired";
    public const string OfferChanged = "OfferChanged";
    public const string PriceChanged = "PriceChanged";
    public const string AvailabilityChanged = "AvailabilityChanged";
    public const string TypeVersionCreated = "TypeVersionCreated";

    /// <summary>The whole item as it now is, one per product per save, for consumers that index items.</summary>
    public const string ItemChanged = "ItemChanged";

    /// <summary>The item was deleted or retired and should leave any index.</summary>
    public const string ItemRemoved = "ItemRemoved";

    /// <summary>Whole-item events go to their own topic, so indexers don't read every row change.</summary>
    public static bool IsItemEvent(string type) => type is ItemChanged or ItemRemoved;

    public static readonly IReadOnlySet<string> InvalidatesReadCache = new HashSet<string>(StringComparer.Ordinal)
    {
        ProductUpdated, ProductPublished, ProductRetired, OfferChanged, PriceChanged, AvailabilityChanged,
    };
}

public static class AggregateTypes
{
    public const string Product = "product";
    public const string Offer = "offer";
    public const string Price = "price";
    public const string Availability = "availability";
    public const string ProductType = "product_type";
}

/// <summary>
/// An event as the relay hands it to the publisher. <see cref="WorkspaceId"/> is the
/// business the event is about (the catalog's tenant for row-level security);
/// <see cref="OwnerId"/> is the account that owns it; <see cref="UserId"/> is who made
/// the change. On the wire it is a <see cref="CatalogEventMessage"/>.
/// </summary>
public sealed record EventEnvelope(
    Guid EventId,
    Guid WorkspaceId,
    string? OwnerId,
    string? UserId,
    string AggregateType,
    Guid AggregateId,
    string Type,
    int SchemaVersion,
    DateTime OccurredAt,
    JsonElement Payload)
{
    public static readonly JsonSerializerOptions WireOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>
/// The message on both catalog topics (<c>catalog.events</c> and <c>catalog.items</c>),
/// named the way Parrot's <c>MessagePersisted</c> names things: <c>tenant_id</c> is the
/// account that owns the workspace (null until the catalog has been told it),
/// <c>workspace_id</c> is the business, and <c>user_id</c> is who made the change.
/// <c>event_id</c> is the outbox row id; consumers dedupe on it (ADR-005).
/// </summary>
public sealed record CatalogEventMessage(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("tenant_id")] string? TenantId,
    [property: JsonPropertyName("workspace_id")] Guid? WorkspaceId,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("aggregate_type")] string AggregateType,
    [property: JsonPropertyName("aggregate_id")] Guid AggregateId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("occurred_at")] DateTime OccurredAt,
    [property: JsonPropertyName("payload")] JsonElement Payload)
{
    public static CatalogEventMessage From(EventEnvelope envelope) => new(
        envelope.EventId, envelope.OwnerId, envelope.WorkspaceId, envelope.UserId, envelope.AggregateType,
        envelope.AggregateId, envelope.Type, envelope.SchemaVersion, envelope.OccurredAt, envelope.Payload);

    /// <summary>
    /// Back to an envelope, for in-repo consumers. A message published before
    /// <c>workspace_id</c> existed carried the workspace in <c>tenant_id</c>, so that is
    /// read when <c>workspace_id</c> is missing. Null when neither names a workspace.
    /// </summary>
    public EventEnvelope? ToEnvelope()
    {
        Guid? workspace = WorkspaceId is { } w && w != Guid.Empty ? w
            : Guid.TryParse(TenantId, out Guid legacy) && legacy != Guid.Empty ? legacy
            : null;
        if (workspace is null)
        {
            return null;
        }

        string? owner = WorkspaceId is null ? null : TenantId;
        return new EventEnvelope(EventId, workspace.Value, owner, UserId, AggregateType, AggregateId, Type, SchemaVersion, OccurredAt, Payload);
    }
}

/// <summary>Publishes an envelope to the bus. Implemented by Kafka in infrastructure and by a recorder in tests.</summary>
public interface IEventBusPublisher
{
    Task PublishAsync(EventEnvelope envelope, CancellationToken ct);
}
