using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Application.Agents;

namespace Parrot.Application.Contracts.BusinessSources;

/// <summary>
/// Published to <c>business.knowledge.sources</c> whenever the workspace's active source
/// set or its metadata changes. The entire message is snake_case with explicit
/// [JsonPropertyName] attributes — the bus options write camelCase by default, so every
/// name is given here rather than left to the policy (same pattern as AgentConfigChangedEvent).
/// </summary>
public sealed record BusinessSourcesUpdatedMessage
{
    /// <summary>Stable identifier for this specific Kafka event; consumers use it for idempotency.</summary>
    [JsonPropertyName("event_id")]
    public required string EventId { get; init; }

    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = "BusinessSourcesUpdated";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("workspace_id")]
    public required string WorkspaceId { get; init; }

    /// <summary>
    /// Monotonically increasing per workspace. Omni must use this — not occurred_at — to
    /// determine the latest snapshot. Stale snapshots (lower version) must be discarded.
    /// </summary>
    [JsonPropertyName("snapshot_version")]
    public long SnapshotVersion { get; init; }

    [JsonPropertyName("occurred_at")]
    [JsonConverter(typeof(AgentEventInstantConverter))]
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Set only when this event was triggered by an explicit refresh request.
    /// Distinct from event_id (identifies this Kafka message) and snapshot_version
    /// (identifies the workspace state version).
    /// </summary>
    [JsonPropertyName("refresh_request_id")]
    public string? RefreshRequestId { get; init; }

    [JsonPropertyName("data")]
    public required BusinessData Data { get; init; }

    [JsonPropertyName("sources")]
    public required IReadOnlyList<SourceEntry> Sources { get; init; }
}

public sealed record BusinessData
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record SourceEntry
{
    /// <summary>Stable composite identifier: e.g. <c>whatsapp:17654321098</c>.</summary>
    [JsonPropertyName("source_id")]
    public required string SourceId { get; init; }

    [JsonPropertyName("external_id")]
    public required string ExternalId { get; init; }

    [JsonPropertyName("connection_id")]
    public required string ConnectionId { get; init; }

    /// <summary>"whatsapp" | "instagram" | "facebook"</summary>
    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    /// <summary>"profile" | "catalog"</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("handle")]
    public string? Handle { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>
    /// Channel-specific provider identifiers that do not fit the generic model.
    /// WhatsApp: { "waba_id": "...", "phone_number": "+1 234 567 8900" }
    /// Other channels: null. Field names subject to Omni agreement (open item O5).
    /// </summary>
    [JsonPropertyName("provider_data")]
    public JsonElement? ProviderData { get; init; }

    /// <summary>
    /// Available profile metadata as a JSON object; missing fields omitted.
    /// Structure subject to Omni agreement (open item O4).
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("captured_at")]
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
