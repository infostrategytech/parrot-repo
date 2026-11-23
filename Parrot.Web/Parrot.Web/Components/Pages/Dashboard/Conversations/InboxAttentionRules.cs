using Parrot.Web.Features.Dashboard.TaskRequests;

namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

public enum InboxNeed { None, AgentAnswer, Approval, TeammateAnswer, ReplyReady, CustomerReply }

/// <summary>Needs you is a personal conversation-input queue, never an open/urgent-task queue.
/// The API must resolve responsibility and access; this presentation rule is not an authorisation boundary.</summary>
public static class InboxAttentionRules
{
    /// <param name="answersAgents">The person may answer agents' questions and decide their approvals
    /// (conversations.take-over). An open question or approval with nobody named on it is theirs then.</param>
    public static InboxNeed Reason(ConversationItem conversation, string personId, IEnumerable<TaskQuestion> questions, bool answersAgents = false)
    {
        if (string.IsNullOrWhiteSpace(personId)) return InboxNeed.None;
        var responsible = conversation.Attention?.ResponsiblePersonId ?? conversation.Assignee?.Id;
        var mine = !string.IsNullOrWhiteSpace(responsible) && responsible == personId;
        // Nobody is named until conversations can be assigned: the agent's question waits for whoever can answer it.
        var agentsAskMe = mine || (answersAgents && string.IsNullOrWhiteSpace(responsible));
        if (agentsAskMe && conversation.NeedsAnswer) return InboxNeed.AgentAnswer;
        if (agentsAskMe && conversation.PendingApproval is not null) return InboxNeed.Approval;
        var question = questions.FirstOrDefault(q => q.ConversationId == conversation.Id && q.Waits(personId));
        if (question is not null) return question.Answered ? InboxNeed.ReplyReady : InboxNeed.TeammateAnswer;
        return mine && conversation.Attention is { ReplyNeeded: true }
            && conversation.AiState != AiState.Active && conversation.Status != ConversationStatus.Resolved
            ? InboxNeed.CustomerReply : InboxNeed.None;
    }

    public static string? Label(InboxNeed need) => need switch
    {
        InboxNeed.AgentAnswer => "Needs your answer",
        InboxNeed.Approval => "Needs your approval",
        InboxNeed.TeammateAnswer => "A teammate needs your answer",
        InboxNeed.ReplyReady => "Teammate answered · Reply needed",
        InboxNeed.CustomerReply => "Reply needed",
        _ => null,
    };
}
