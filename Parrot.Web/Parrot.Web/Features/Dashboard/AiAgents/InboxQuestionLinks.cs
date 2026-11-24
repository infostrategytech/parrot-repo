using Parrot.Web.Components.Pages.Dashboard.AiAgents;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>Operations pages are entry points, never independent queues or answer surfaces.</summary>
public static class InboxQuestionLinks
{
    public static string Queue(string? agentId = null) => "/dashboard/inbox?view=needs-you&questions=true"
        + (string.IsNullOrWhiteSpace(agentId) ? "" : "&agent=" + Uri.EscapeDataString(agentId));

    // One question opens its conversation, at the message when the API names one; several open the queue.
    public static string Review(IReadOnlyList<WaitingQuestion> questions, string? agentId = null)
    {
        if (questions.Count != 1 || questions[0] is not { ConversationId: { Length: > 0 } conversation } question)
            return Queue(agentId);
        return Queue(agentId).Replace("/dashboard/inbox?", "/dashboard/inbox/" + Uri.EscapeDataString(conversation) + "?", StringComparison.Ordinal)
            + (question.MessageId is { Length: > 0 } message ? "&message=" + Uri.EscapeDataString(message) : "");
    }

    // Backward-compatible read of older activity feeds. Never attribute a same-named question to two agents.
    public static bool BelongsTo(WaitingQuestion question, AiAgent agent, IReadOnlyList<AiAgent> agents) =>
        question.AgentId is { Length: > 0 } id ? id == agent.Id
        : question.AgentName == agent.Name && agents.Count(a => a.Name == agent.Name) == 1;
}
