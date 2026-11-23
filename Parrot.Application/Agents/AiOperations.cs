using Parrot.Application.Contracts.Conversations;
using Parrot.Application.Conversations;

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
    string AgentName, string Customer, string Question, string? ApprovalId, string? EscalationId, string? Priority, DateTime OccurredAt)
{
    /// <summary>The agent that asked, by the orchestrator's id; null when its turn was never reported.</summary>
    public string? AgentId { get; init; }

    /// <summary>The inbox conversation it was asked in, named as the inbox names its rows: "{connectionId}:{sender}".</summary>
    required public string ConversationId { get; init; }
}

/// <summary>What is open in one inbox conversation: an agent's question, an approval, or both, and the agent that asked.</summary>
public sealed record ConversationQuestions(
    Guid ConnectionId, string Sender, bool NeedsAnswer, ConversationApproval? Approval, ConversationAgentRef? Agent);

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

    /// <summary>
    /// The open approvals and escalations, oldest first, each with the inbox conversation it was asked in. One whose
    /// conversation is not in the inbox is left out, since it can't be answered there. <paramref name="reach"/> is the
    /// connections a channel-limited teammate can open (null for everyone else); questions on others are left out.
    /// </summary>
    Task<IReadOnlyList<WaitingItemResponse>> WaitingAsync(string ownerUserId, IReadOnlySet<Guid>? reach, CancellationToken cancellationToken = default);

    /// <summary>The same open items as <see cref="WaitingAsync"/>, one entry per inbox conversation.</summary>
    Task<IReadOnlyList<ConversationQuestions>> OpenByConversationAsync(string ownerUserId, IReadOnlySet<Guid>? reach, CancellationToken cancellationToken = default);

    Task<ApprovalDecisionResponse> DecideApprovalAsync(
        string ownerUserId, string approvalId, ApprovalDecisionRequest request, CancellationToken cancellationToken = default);
}
