using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Application.Messaging;

namespace Parrot.Application.Agents;

/// <summary>
/// An event on the agent events topic as the orchestrator writes it:
/// snake_case names, the instant as an ISO string with an offset, and a
/// payload whose shape depends on the type. Every field is optional here so
/// that an event this API does not understand is skipped, not redelivered
/// for ever; <see cref="AgentActivityMapper"/> decides what is usable.
/// </summary>
public sealed class AgentEventEnvelope
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; set; }

    [JsonPropertyName("conversation_key")]
    public string? ConversationKey { get; set; }

    [JsonPropertyName("turn_id")]
    public string? TurnId { get; set; }

    [JsonPropertyName("occurred_at")]
    public string? OccurredAt { get; set; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("schema_version")]
    public int? SchemaVersion { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }
}

/// <summary>Turns an event into the record the activity store keeps, or says why it cannot.</summary>
public static class AgentActivityMapper
{
    /// <summary>Payload bytes above this size are dropped rather than stored verbatim.</summary>
    public const int MaxPayloadBytes = 4096;

    public static bool TryMap(AgentEventEnvelope envelope, DateTime receivedAtUtc, out AgentActivityRecord? record, out string reason)
    {
        record = null;

        if (string.IsNullOrWhiteSpace(envelope.Type))
        {
            reason = "no_type";
            return false;
        }

        if (string.IsNullOrWhiteSpace(envelope.TenantId))
        {
            reason = "no_tenant";
            return false;
        }

        JsonElement payload = envelope.Payload is { ValueKind: JsonValueKind.Object } element ? element : default;
        string type = envelope.Type.Trim();
        string? turnId = envelope.TurnId;
        string? status = Text(payload, "status");
        string? approvalId = Text(payload, "approval_id");
        string? escalationId = Text(payload, "escalation_id");

        string? key = type switch
        {
            AgentActivityTypes.TurnCompleted when !string.IsNullOrWhiteSpace(turnId) => $"{turnId}:{status ?? "unknown"}",
            AgentActivityTypes.ApprovalRequested when !string.IsNullOrWhiteSpace(approvalId) => approvalId,
            AgentActivityTypes.EscalationOpened when !string.IsNullOrWhiteSpace(escalationId) => escalationId,
            _ => null,
        };

        if (key is null)
        {
            reason = type is AgentActivityTypes.TurnCompleted or AgentActivityTypes.ApprovalRequested or AgentActivityTypes.EscalationOpened
                ? "no_key"
                : "unhandled_type";
            return false;
        }

        record = new AgentActivityRecord
        {
            OwnerUserId = envelope.TenantId,
            Type = type,
            EventKey = key,
            ConversationKey = envelope.ConversationKey,
            TurnId = turnId,
            AgentId = Text(payload, "agent_id"),
            Contributors = Contributors(payload),
            Decision = Text(payload, "decision"),
            Confidence = Number(payload, "confidence"),
            Status = status,
            CustomerRef = Text(payload, "customer_ref"),
            CustomerName = Text(payload, "customer_name"),
            Channel = Text(payload, "channel")?.ToLowerInvariant(),
            Reply = Text(payload, "reply"),
            EscalationId = escalationId,
            ApprovalId = approvalId,
            Priority = Text(payload, "priority"),
            Problem = Text(payload, "problem"),
            Tool = Text(payload, "tool"),
            Reason = Text(payload, "reason"),
            OccurredAt = Instant(envelope.OccurredAt) ?? receivedAtUtc,
            CorrelationId = envelope.CorrelationId,
            Payload = payload.ValueKind == JsonValueKind.Object ? Cap(payload.GetRawText()) : "{}",
        };
        reason = string.Empty;
        return true;
    }

    private static string Cap(string raw) => raw.Length <= MaxPayloadBytes ? raw : "{}";

    // Read on arrival, not from the stored payload: that is dropped when the reply makes it too big. Ids and status only,
    // as many as the AI service asks at most; an entry without an id is left out.
    private static List<TurnContributor>? Contributors(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("contributors", out JsonElement list)
            || list.ValueKind != JsonValueKind.Array) return null;

        List<TurnContributor> read = [];
        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (read.Count == ReplyContributors.MaxContributors) break;
            if (Text(entry, "agent_id") is not { Length: > 0 } agentId) continue;
            read.Add(new TurnContributor(agentId, Text(entry, "status")?.ToLowerInvariant() ?? "unknown"));
        }

        return read.Count == 0 ? null : read;
    }

    /// <summary>The instant the orchestrator wrote, in UTC; null when it is missing or not a date.</summary>
    public static DateTime? Instant(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at)
            ? at.UtcDateTime
            : null;

    private static string? Text(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
    }

    private static double? Number(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double number)
            ? number
            : null;
}
