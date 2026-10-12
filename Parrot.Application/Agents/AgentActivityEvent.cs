namespace Parrot.Application.Agents;

/// <summary>
/// What the API pushes to an owner's live connections, as <c>agentActivity</c>,
/// when the orchestrator reports something: enough for a screen to refresh
/// what it shows, never the record itself.
/// </summary>
public sealed record AgentActivityEvent
{
    required public string Type { get; init; }

    public string? TurnId { get; init; }

    public string? ConversationKey { get; init; }

    public string? AgentId { get; init; }

    public string? Status { get; init; }

    public string? Decision { get; init; }

    public double? Confidence { get; init; }

    public string? Channel { get; init; }

    public string? CustomerRef { get; init; }

    public string? CustomerName { get; init; }

    public string? Reply { get; init; }

    public string? ApprovalId { get; init; }

    public string? EscalationId { get; init; }

    public string? Priority { get; init; }

    public string? Problem { get; init; }

    public string? Tool { get; init; }

    public string? Reason { get; init; }

    required public DateTime OccurredAt { get; init; }

    public static AgentActivityEvent From(AgentActivityRecord record) => new()
    {
        Type = record.Type,
        TurnId = record.TurnId,
        ConversationKey = record.ConversationKey,
        AgentId = record.AgentId,
        Status = record.Status,
        Decision = record.Decision,
        Confidence = record.Confidence,
        Channel = record.Channel,
        CustomerRef = record.CustomerRef,
        CustomerName = record.CustomerName,
        Reply = record.Reply,
        ApprovalId = record.ApprovalId,
        EscalationId = record.EscalationId,
        Priority = record.Priority,
        Problem = record.Problem,
        Tool = record.Tool,
        Reason = record.Reason,
        OccurredAt = record.OccurredAt,
    };
}
