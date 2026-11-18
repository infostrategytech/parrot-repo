using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Parrot.Application.Agents;
using Parrot.Application.Messaging;

namespace Parrot.Application.Contracts.BusinessSources;

/// <summary>
/// Published to <c>business.knowledge.sources</c> whenever the workspace's active source
/// set or its metadata changes. camelCase, like <c>messages.persisted</c>; every name is
/// spelled out so a change to the bus options can never change this contract.
/// </summary>
public sealed record BusinessSourcesUpdatedMessage
{
    /// <summary>Stable identifier for this specific Kafka event; consumers use it for idempotency.</summary>
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = "BusinessSourcesUpdated";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("workspaceId")]
    public required string WorkspaceId { get; init; }

    /// <summary>
    /// Monotonically increasing per workspace. Omni must use this — not occurred_at — to
    /// determine the latest snapshot. Stale snapshots (lower version) must be discarded.
    /// </summary>
    [JsonPropertyName("snapshotVersion")]
    public long SnapshotVersion { get; init; }

    [JsonPropertyName("occurredAt")]
    [JsonConverter(typeof(AgentEventInstantConverter))]
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Set only when this event was triggered by an explicit refresh request.
    /// Distinct from event_id (identifies this Kafka message) and snapshot_version
    /// (identifies the workspace state version).
    /// </summary>
    [JsonPropertyName("refreshRequestId")]
    public string? RefreshRequestId { get; init; }

    [JsonPropertyName("data")]
    public required BusinessData Data { get; init; }

    [JsonPropertyName("sources")]
    public required IReadOnlyList<SourceEntry> Sources { get; init; }

    /// <summary>
    /// Reads a payload the outbox stored, so it is published as the typed message (not a JSON string).
    /// Rows queued before the contract moved to camelCase still hold snake_case names; they are
    /// read the same way, so they go out in the current shape.
    /// </summary>
    public static BusinessSourcesUpdatedMessage FromStoredPayload(string payload)
    {
        JsonNode node = JsonNode.Parse(payload) ?? throw new InvalidOperationException("Outbox payload is empty.");
        CamelCaseNames(node);
        return node.Deserialize<BusinessSourcesUpdatedMessage>(MessageJson.Options)
            ?? throw new InvalidOperationException("Outbox payload deserialized as null.");
    }

    private static void CamelCaseNames(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array) CamelCaseNames(item);
            return;
        }

        if (node is not JsonObject properties) return;
        foreach (KeyValuePair<string, JsonNode?> property in properties.ToList())
        {
            CamelCaseNames(property.Value);
            if (!property.Key.Contains('_')) continue;
            properties.Remove(property.Key);
            properties[ToCamelCase(property.Key)] = property.Value;
        }
    }

    private static string ToCamelCase(string snakeCase)
    {
        string[] words = snakeCase.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return words[0] + string.Concat(words.Skip(1).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}

public sealed record BusinessData
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record SourceEntry
{
    /// <summary>Stable composite identifier: e.g. <c>whatsapp:17654321098</c>.</summary>
    [JsonPropertyName("sourceId")]
    public required string SourceId { get; init; }

    [JsonPropertyName("externalId")]
    public required string ExternalId { get; init; }

    [JsonPropertyName("connectionId")]
    public required string ConnectionId { get; init; }

    /// <summary>"whatsapp" | "instagram" | "facebook"</summary>
    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    /// <summary>"profile" | "catalog"</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("handle")]
    public string? Handle { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>
    /// Channel-specific provider identifiers that do not fit the generic model.
    /// WhatsApp: { "wabaId", "phoneNumberId" } (or "catalogId" for a catalog);
    /// Instagram: { "accountId" }; Facebook: { "pageId" }.
    /// </summary>
    [JsonPropertyName("providerData")]
    public JsonElement? ProviderData { get; init; }

    /// <summary>
    /// Available profile metadata as a JSON object; missing fields omitted.
    /// Structure subject to Omni agreement (open item O4).
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("capturedAt")]
    [JsonConverter(typeof(NullableAgentEventInstantConverter))]
    public DateTimeOffset? CapturedAt { get; init; }
}

/// <summary>
/// Nullable variant of <see cref="AgentEventInstantConverter"/> for optional timestamp fields.
/// Writes null for null values; reads null for missing or null JSON values.
/// </summary>
public sealed class NullableAgentEventInstantConverter : JsonConverter<DateTimeOffset?>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'";

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        string? raw = reader.GetString();
        return raw is null ? null : DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
    }
}
