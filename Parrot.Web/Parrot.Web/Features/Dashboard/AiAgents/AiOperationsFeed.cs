using Parrot.Web.Components.Pages.Dashboard.AiAgents;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// One agent's day (or several agents' added up): the conversations it took,
/// how many it finished on its own, how many it passed to the team and how
/// quickly the team replied to those, and the tasks it created.
/// </summary>
public sealed record AgentToday(int Conversations, int AnsweredAlone, int PassedToTeam, int TasksCreated = 0, int? TeamReplyMinutes = null)
{
    public static readonly AgentToday None = new(0, 0, 0);

    /// <summary>Answered alone, as a share of today's conversations (0 when there were none).</summary>
    public int AnsweredShare => Conversations == 0 ? 0 : (int)Math.Round(100.0 * AnsweredAlone / Conversations);

    /// <summary>Added up; the team's reply time is averaged over the conversations passed to it.</summary>
    public static AgentToday Sum(IEnumerable<AgentToday> days)
    {
        var list = days.ToList();
        var passed = list.Sum(d => d.PassedToTeam);
        var timed = list.Where(d => d.TeamReplyMinutes is not null && d.PassedToTeam > 0).ToList();
        int? reply = timed.Count == 0 ? null
            : (int)Math.Round(timed.Sum(d => d.TeamReplyMinutes!.Value * d.PassedToTeam) / (double)timed.Sum(d => d.PassedToTeam));
        return new(list.Sum(d => d.Conversations), list.Sum(d => d.AnsweredAlone), passed, list.Sum(d => d.TasksCreated), reply);
    }
}

/// <summary>A customer's question an agent could not answer, waiting for the owner.</summary>
public sealed record WaitingQuestion(string AgentName, string Customer, string Question);

public enum ConversationOutcome { Answered, PassedToTeam, TaskCreated }

/// <summary>One of an agent's latest conversations: who, where, how it ended, and when.</summary>
public sealed record RecentConversation(string Customer, string Channel, ConversationOutcome Outcome, string? Task, int MinutesAgo);

/// <summary>
/// What AI Operations and an agent's page show about the agents' work:
/// today's numbers (for every channel, or one), the last seven days, the
/// latest conversations, and the questions waiting for the owner. Demo mode
/// answers with sample figures; real mode has no operations feed yet, so the
/// pages hide what they cannot show (see agents-backend-requests).
/// </summary>
public interface IAiOperationsFeed
{
    bool Available { get; }

    /// <summary>Today, on every channel it answers, or on one (a channel key such as "instagram").</summary>
    AgentToday Today(AiAgent agent, string? channel = null);

    /// <summary>Conversations per day for the last seven days, oldest first, today last.</summary>
    IReadOnlyList<int> Week(AiAgent agent, string? channel = null);

    /// <summary>The latest conversations, newest first.</summary>
    IReadOnlyList<RecentConversation> Recent(AiAgent agent, string? channel = null);

    IReadOnlyList<WaitingQuestion> Waiting(IReadOnlyList<AiAgent> agents);
}

/// <summary>Real mode: no operations feed yet. Only today's message count is known.</summary>
public sealed class UnavailableAiOperationsFeed : IAiOperationsFeed
{
    public bool Available => false;

    public AgentToday Today(AiAgent agent, string? channel = null) => new(agent.MessagesToday, 0, 0);

    public IReadOnlyList<int> Week(AiAgent agent, string? channel = null) => [];

    public IReadOnlyList<RecentConversation> Recent(AiAgent agent, string? channel = null) => [];

    public IReadOnlyList<WaitingQuestion> Waiting(IReadOnlyList<AiAgent> agents) => [];
}
