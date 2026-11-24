namespace Parrot.Application.Agents;

/// <summary>The event types the orchestrator publishes, and the one the API publishes back.</summary>
public static class AgentActivityTypes
{
    public const string TurnCompleted = "agent.turn.completed";
    public const string ApprovalRequested = "agent.approval.requested";
    public const string EscalationOpened = "agent.escalation.opened";
    public const string ConfigChanged = AgentConfigChangedEvent.EventType;

    /// <summary>The statuses a turn ends in (the orchestrator's TurnStatus).</summary>
    public static class TurnStatus
    {
        public const string Completed = "completed";
        public const string Escalated = "escalated";
        public const string AwaitingApproval = "awaiting_approval";
        public const string Failed = "failed";
        public const string Skipped = "skipped";
    }

    /// <summary>What the owner can decide about an approval.</summary>
    public static class Resolution
    {
        public const string Approved = "approved";
        public const string Denied = "denied";
    }
}

/// <summary>
/// One thing an agent did, as the orchestrator reported it: a turn it
/// finished, an approval it asked for, or an escalation it opened. Stored as
/// it arrived; the figures on the AI Operations screens are worked out from
/// these rows, never kept as counters.
/// </summary>
public sealed class AgentActivityRecord
{
    required public string OwnerUserId { get; init; }

    required public string Type { get; init; }

    /// <summary>
    /// What makes a redelivery the same event: the turn and the status it
    /// reached (one turn reports twice when it waited for an approval), or
    /// the approval or escalation id. Unique with <see cref="Type"/>.
    /// </summary>
    required public string EventKey { get; init; }

    public string? ConversationKey { get; init; }

    public string? TurnId { get; init; }

    public string? AgentId { get; init; }

    public string? Decision { get; init; }

    public double? Confidence { get; init; }

    public string? Status { get; init; }

    public string? CustomerRef { get; init; }

    public string? CustomerName { get; init; }

    public string? Channel { get; init; }

    public string? Reply { get; init; }

    public string? EscalationId { get; init; }

    public string? ApprovalId { get; init; }

    public string? Priority { get; init; }

    public string? Problem { get; init; }

    public string? Tool { get; init; }

    public string? Reason { get; init; }

    required public DateTime OccurredAt { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>The event's payload as it arrived, for anything the columns above leave out.</summary>
    required public string Payload { get; init; }

    /// <summary>When the owner decided an approval (set on it and on the escalation opened for it).</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>"approved" or "denied".</summary>
    public string? Resolution { get; set; }

    public string? ResolvedBy { get; set; }

    public string? Instruction { get; set; }
}

/// <summary>Where the agents' activity is kept, one document per event, inserted once.</summary>
public interface IAgentActivityStore
{
    /// <summary>Stores the event, or returns false when the same event (by type and key) is already there.</summary>
    Task<bool> InsertAsync(AgentActivityRecord record, CancellationToken cancellationToken = default);

    /// <summary>The owner's events of these types that occurred in [from, to), oldest first.</summary>
    Task<IReadOnlyList<AgentActivityRecord>> ListAsync(
        string ownerUserId, IReadOnlyCollection<string> types, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>The owner's latest events of one type, newest first.</summary>
    Task<IReadOnlyList<AgentActivityRecord>> ListRecentAsync(string ownerUserId, string type, int limit, CancellationToken cancellationToken = default);

    /// <summary>Every event of the owner's that belongs to one of these turns.</summary>
    Task<IReadOnlyList<AgentActivityRecord>> ListByTurnIdsAsync(
        string ownerUserId, IReadOnlyCollection<string> turnIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the approval, and any escalation opened for it, decided. Returns
    /// how many records that touched: 0 when the owner has no such approval waiting.
    /// </summary>
    Task<int> MarkApprovalDecidedAsync(
        string ownerUserId,
        string approvalId,
        string resolution,
        string decidedBy,
        string? instruction,
        DateTime decidedAtUtc,
        CancellationToken cancellationToken = default);
}
