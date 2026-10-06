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

/// <summary>The envelope on the bus. <c>event_id</c> is the outbox row id; consumers dedupe on it (ADR-005).</summary>
public sealed record EventEnvelope(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("tenant_id")] Guid TenantId,
    [property: JsonPropertyName("aggregate_type")] string AggregateType,
    [property: JsonPropertyName("aggregate_id")] Guid AggregateId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("occurred_at")] DateTime OccurredAt,
    [property: JsonPropertyName("payload")] JsonElement Payload)
{
    public static readonly JsonSerializerOptions WireOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>Publishes an envelope to the bus. Implemented by Kafka in infrastructure and by a recorder in tests.</summary>
public interface IEventBusPublisher
{
    Task PublishAsync(EventEnvelope envelope, CancellationToken ct);
}
