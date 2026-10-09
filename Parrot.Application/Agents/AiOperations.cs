namespace Parrot.Application.Agents;

/// <summary>One agent's day, or every agent's added up: the AI Operations figures.</summary>
public sealed record AgentTodayResponse(int Conversations, int AnsweredAlone, int PassedToTeam, int TasksCreated, int? TeamReplyMinutes);

/// <summary>How a turn ended, as the screens word it.</summary>
public static class ConversationOutcomes
{
    public const string Answered = "Answered";
    public const string PassedToTeam = "PassedToTeam";
    public const string TaskCreated = "TaskCreated";
}

/// <summary>One of the latest conversations an agent took: who, where, how it ended, and when.</summary>
public sealed record RecentConversationResponse(
    string Customer, string Channel, string Outcome, string? Task, int MinutesAgo, string TurnId, string ConversationKey);

/// <summary>Something an agent could not settle on its own, waiting for the owner.</summary>
public sealed record WaitingItemResponse(
    string AgentName, string Customer, string Question, string? ApprovalId, string? EscalationId, string? Priority, DateTime OccurredAt);

public sealed record ApprovalDecisionRequest(string Decision, string? Instruction);

public sealed record ApprovalDecisionResponse(string ApprovalId, string Decision, string? TurnId, string? Status, string? Reply);

/// <summary>
/// The AI Operations figures and the owner's decisions, every one scoped to
/// the owner: the activity is read for the owner alone, and an approval is
/// only decided when it was asked of that owner.
/// </summary>
public interface IAiOperationsService
{
    Task<AgentTodayResponse> TodayAsync(string ownerUserId, string? agentId, string? channel, CancellationToken cancellationToken = default);

    /// <summary>Conversations per day for the last seven days, oldest first, today last.</summary>
    Task<IReadOnlyList<int>> WeekAsync(string ownerUserId, string? agentId, string? channel, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecentConversationResponse>> RecentAsync(
        string ownerUserId, string? agentId, string? channel, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WaitingItemResponse>> WaitingAsync(string ownerUserId, CancellationToken cancellationToken = default);

    Task<ApprovalDecisionResponse> DecideApprovalAsync(
        string ownerUserId, string approvalId, ApprovalDecisionRequest request, CancellationToken cancellationToken = default);
}
