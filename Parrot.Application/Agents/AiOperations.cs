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
/// The statuses a decision can come back with besides the agent's own turn statuses. The first two are the AI
/// service's words; <see cref="ConversationBusy"/> is this API's name for its 409 CONVERSATION_BUSY answer.
/// </summary>
public static class ApprovalDecisionStatus
{
    /// <summary>Someone decided it first. Nothing changed.</summary>
    public const string AlreadyDecided = "already_decided";

    /// <summary>The decision is recorded, but no turn of the agent's was waiting on it any more.</summary>
    public const string NoTurn = "no_turn";

    /// <summary>The decision is recorded; the agent is replying in that conversation and acts on it once it has.</summary>
    public const string ConversationBusy = "conversation_busy";
}

/// <summary>
/// The AI Operations figures and the team's decisions, every one scoped to
/// the workspace: the activity is read for it alone, and an approval is only
/// decided while it waits on the workspace's own waiting list.
/// </summary>
public interface IAiOperationsService
{
    Task<AgentTodayResponse> TodayAsync(string ownerUserId, string? agentId, string? channel, CancellationToken cancellationToken = default);

    /// <summary>Conversations per day for the last seven days, oldest first, today last.</summary>
    Task<IReadOnlyList<int>> WeekAsync(string ownerUserId, string? agentId, string? channel, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecentConversationResponse>> RecentAsync(
        string ownerUserId, string? agentId, string? channel, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// What is waiting for the team, longest-waiting first: the AI service's open items, one per conversation, each with
    /// the inbox conversation it belongs to. One whose conversation is not in the inbox is left out, since it can't be
    /// answered there. <paramref name="userToken"/> is the caller's own session token, which the AI service reads the
    /// workspace from. <paramref name="reach"/> is the connections a channel-limited teammate can open (null for
    /// everyone else); items on others are left out. When the AI service doesn't answer, nothing is waiting.
    /// </summary>
    Task<IReadOnlyList<WaitingItemResponse>> WaitingAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, CancellationToken cancellationToken = default);

    /// <summary>The same open items as <see cref="WaitingAsync"/>, one entry per inbox conversation.</summary>
    Task<IReadOnlyList<ConversationQuestions>> OpenByConversationAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, CancellationToken cancellationToken = default);

    /// <summary>The agents' open questions in one conversation, as the messages the inbox shows them as.</summary>
    Task<IReadOnlyList<ConversationMessage>> OpenQuestionsAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, Guid connectionId, string sender, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an agent's open question in this conversation handled, as the caller, without a reply, and says so in the
    /// chat. Throws <see cref="Domain.Exceptions.NotFoundException"/> when no such question is open there.
    /// </summary>
    Task CloseQuestionAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, Guid connectionId, string sender, string questionId, string closedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides an approval as <paramref name="decidedBy"/>, the signed-in person. Only one still waiting on an open item
    /// in the workspace, on a channel the caller reaches, can be decided; anything else is
    /// <see cref="Domain.Exceptions.NotFoundException"/>, the same whatever the reason. The response's Status is one of
    /// <see cref="ApprovalDecisionStatus"/> when someone decided it first, no turn was waiting on it, or the agent will
    /// act on it shortly.
    /// </summary>
    Task<ApprovalDecisionResponse> DecideApprovalAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, string decidedBy, string approvalId, ApprovalDecisionRequest request,
        CancellationToken cancellationToken = default);
}
