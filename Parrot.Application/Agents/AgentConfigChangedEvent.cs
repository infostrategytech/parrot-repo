using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Application.Agents;

/// <summary>
/// Written to the agent events topic when an agent is created, changed,
/// switched or deleted, so the orchestrator drops the configuration it
/// cached for the tenant. It is spelled the way the orchestrator's own events
/// are (snake_case, an ISO instant with an offset): the bus options write
/// camelCase, so every name is given here rather than left to the policy.
/// </summary>
public sealed record AgentConfigChangedEvent
{
    public const string EventType = "agent.config.changed";

    [JsonPropertyName("type")]
    public string Type { get; init; } = EventType;

    [JsonPropertyName("tenant_id")]
    required public string TenantId { get; init; }

    [JsonPropertyName("conversation_key")]
    public string? ConversationKey { get; init; }

    [JsonPropertyName("turn_id")]
    public string? TurnId { get; init; }

    [JsonPropertyName("occurred_at")]
    [JsonConverter(typeof(AgentEventInstantConverter))]
    required public DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("payload")]
    required public AgentConfigChangedPayload Payload { get; init; }
}

public sealed record AgentConfigChangedPayload
{
    [JsonPropertyName("agent_id")]
    required public string AgentId { get; init; }

    [JsonPropertyName("version")]
    required public int Version { get; init; }

    [JsonPropertyName("channels")]
    required public IReadOnlyList<string> Channels { get; init; }

    [JsonPropertyName("is_live")]
    required public bool IsLive { get; init; }

    /// <summary>True on the last event an agent sends: the owner deleted it.</summary>
    [JsonPropertyName("deleted")]
    public bool Deleted { get; init; }
}

/// <summary>
/// An instant as <c>2026-10-08T12:00:00.000000+00:00</c>: always UTC, always
/// six fractional digits. .NET writes seven, which a Python reader may not
/// take; the orchestrator writes six, so both sides read the same shape.
/// </summary>
public sealed class AgentEventInstantConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
