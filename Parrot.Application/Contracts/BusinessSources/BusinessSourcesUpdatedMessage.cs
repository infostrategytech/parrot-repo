using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Application.Agents;
using Parrot.Application.Messaging;
using Parrot.Domain.Enums;

namespace Parrot.Application.Contracts.BusinessSources;

/// <summary>
/// Published to <c>business.knowledge.sources</c> whenever the workspace's active source
/// set or its metadata changes. camelCase, like <c>messages.persisted</c>; every name is
/// spelled out so a change to the bus options can never change this contract.
/// </summary>
public sealed record BusinessSourcesUpdatedMessage
{
    /// <summary>
    /// Omni's contract version for this event: 1, for this camelCase shape with the snapshot under data.
    /// A separate counter from messages.persisted's 2; Omni refuses any other value.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Stable identifier for this specific Kafka event; consumers use it for idempotency.</summary>
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("occurredAt")]
    [JsonConverter(typeof(AgentEventInstantConverter))]
    public required DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("data")]
    public required BusinessSourcesSnapshot Data { get; init; }

    /// <summary>
    /// Reads a payload the outbox stored, so it is published as the typed message (not a JSON string).
    /// Null for a row queued in an older shape Omni can't read: snake_case, flat camelCase (the same
    /// version number, so the shape decides), or the short-lived schemaVersion 2.
    /// </summary>
    public static BusinessSourcesUpdatedMessage? FromStoredPayload(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        bool isCurrent = root.TryGetProperty("schemaVersion", out JsonElement version)
            && version.ValueKind == JsonValueKind.Number
            && version.GetInt32() == CurrentSchemaVersion
            && root.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("workspaceId", out _);
        if (!isCurrent) return null;

        return root.Deserialize<BusinessSourcesUpdatedMessage>(MessageJson.Options)
            ?? throw new InvalidOperationException("Outbox payload deserialized as null.");
    }
}

/// <summary>The workspace's whole picture: every active source, never a delta.</summary>
public sealed record BusinessSourcesSnapshot
{
    [JsonPropertyName("workspaceId")]
    public required string WorkspaceId { get; init; }

    /// <summary>The workspace's owner, as on <c>messages.persisted</c>: the same for every workspace they own.</summary>
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    [JsonPropertyName("trigger")]
    [JsonConverter(typeof(JsonStringEnumConverter<SnapshotTrigger>))]
    public required SnapshotTrigger Trigger { get; init; }

    /// <summary>
    /// Monotonically increasing per workspace. Omni must use this — not occurredAt — to
    /// determine the latest snapshot. Stale snapshots (lower version) must be discarded.
    /// </summary>
    [JsonPropertyName("snapshotVersion")]
    public long SnapshotVersion { get; init; }

    [JsonPropertyName("business")]
    public required BusinessData Business { get; init; }

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
