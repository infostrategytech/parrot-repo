using Parrot.Web.Components.Pages.Dashboard.AiAgents;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>Operations pages are entry points, never independent queues or answer surfaces.</summary>
public static class InboxQuestionLinks
{
    public static string Queue(string? agentId = null) => "/dashboard/inbox?view=needs-you&questions=true"
        + (string.IsNullOrWhiteSpace(agentId) ? "" : "&agent=" + Uri.EscapeDataString(agentId));

    public static string Review(IReadOnlyList<WaitingQuestion> questions, string? agentId = null)
    {
        if (questions.Count != 1 || questions[0] is not { ConversationId: { Length: > 0 } conversation, MessageId: { Length: > 0 } message })
            return Queue(agentId);
        return Queue(agentId).Replace("/dashboard/inbox?", "/dashboard/inbox/" + Uri.EscapeDataString(conversation) + "?", StringComparison.Ordinal)
            + "&message=" + Uri.EscapeDataString(message);
    }

    // Backward-compatible read of older activity feeds. Never attribute a same-named question to two agents.
    public static bool BelongsTo(WaitingQuestion question, AiAgent agent, IReadOnlyList<AiAgent> agents) =>
        question.AgentId is { Length: > 0 } id ? id == agent.Id
        : question.AgentName == agent.Name && agents.Count(a => a.Name == agent.Name) == 1;
}
