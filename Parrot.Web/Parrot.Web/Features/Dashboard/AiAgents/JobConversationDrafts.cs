using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// "Tell Tolu its job", left halfway (designer, 19 October 2026): an agent is
/// created first and told its job on its own page, one topic at a time, each
/// approval kept as it happens. What the conversation had reached is kept here
/// per agent, so leaving and coming back picks up at the next topic, and the
/// agent's Overview can show how far it got.
/// <para>Kept for the session only: the agents API has nowhere to keep an
/// unfinished conversation yet (agents-backend-requests 31).</para>
/// </summary>
public sealed class JobConversationDrafts
{
    private readonly Dictionary<string, Entry> _drafts = [];

    /// <summary>Where a conversation had got to, for the agent's Overview: its topics and the one next.</summary>
    public sealed record Progress(IReadOnlyList<TopicTrailItem> Topics, string? Next);

    private sealed record Entry(object State, Progress Progress);

    /// <summary>Raised when a conversation is kept or dropped, so a page showing its progress redraws.</summary>
    public event Action? Changed;

    /// <summary>Keeps the conversation as it stands (the editor's own state; nothing else reads it).</summary>
    public void Keep(string agentId, object state, Progress progress)
    {
        _drafts[agentId] = new Entry(state, progress);
        Changed?.Invoke();
    }

    public bool TryGet(string agentId, out object? state)
    {
        var found = _drafts.TryGetValue(agentId, out var entry);
        state = entry?.State;
        return found;
    }

    public Progress? ProgressOf(string agentId) => _drafts.TryGetValue(agentId, out var entry) ? entry.Progress : null;

    /// <summary>The job description is written: nothing left to pick up.</summary>
    public void Drop(string agentId)
    {
        if (_drafts.Remove(agentId)) Changed?.Invoke();
    }
}
