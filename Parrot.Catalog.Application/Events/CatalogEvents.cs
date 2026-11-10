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
/// The envelope on <c>catalog.events</c>, and what the relay hands the publisher for both
/// topics. <c>event_id</c> is the outbox row id; consumers dedupe on it (ADR-005).
/// On this topic <c>tenant_id</c> is the workspace, as it always has been: the cache
/// invalidator and Omni-AI's catalog summaries read it that way. <c>workspace_id</c>
/// says the same under the name every topic will share; <c>user_id</c> is who made the
/// change. The workspace's owner is carried but sent only on <c>catalog.items</c>
/// (see <see cref="ItemEventMessage"/>), where <c>tenant_id</c> means the owner.
/// </summary>
public sealed record EventEnvelope(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("tenant_id")] Guid TenantId,
    [property: JsonIgnore] string? OwnerId,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("aggregate_type")] string AggregateType,
    [property: JsonPropertyName("aggregate_id")] Guid AggregateId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("occurred_at")] DateTime OccurredAt,
    [property: JsonPropertyName("payload")] JsonElement Payload)
{
    /// <summary>The workspace, under the name shared with <c>catalog.items</c> and Parrot's own events.</summary>
    [JsonPropertyName("workspace_id")]
    public Guid WorkspaceId => TenantId;

    public static readonly JsonSerializerOptions WireOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>
/// The message on <c>catalog.items</c>, named the way Parrot's <c>MessagePersisted</c>
/// names things: <c>tenant_id</c> is the account that owns the workspace (null until
/// the catalog has been told it), <c>workspace_id</c> is the business, and
/// <c>user_id</c> is who made the change.
/// </summary>
public sealed record ItemEventMessage(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("tenant_id")] string? TenantId,
    [property: JsonPropertyName("workspace_id")] Guid WorkspaceId,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("aggregate_type")] string AggregateType,
    [property: JsonPropertyName("aggregate_id")] Guid AggregateId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("occurred_at")] DateTime OccurredAt,
    [property: JsonPropertyName("payload")] JsonElement Payload)
{
    public static ItemEventMessage From(EventEnvelope envelope) => new(
        envelope.EventId, envelope.OwnerId, envelope.TenantId, envelope.UserId, envelope.AggregateType,
        envelope.AggregateId, envelope.Type, envelope.SchemaVersion, envelope.OccurredAt, envelope.Payload);
}

/// <summary>Publishes an envelope to the bus. Implemented by Kafka in infrastructure and by a recorder in tests.</summary>
public interface IEventBusPublisher
{
    Task PublishAsync(EventEnvelope envelope, CancellationToken ct);
}
