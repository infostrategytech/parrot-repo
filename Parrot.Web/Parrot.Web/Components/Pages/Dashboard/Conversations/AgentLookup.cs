using Parrot.Web.Components.Pages.Dashboard.AiAgents;

namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

/// <summary>
/// The look an agent has in the Inbox (designer, 15 November 2026: its avatar, never a robot icon), as Tasks shows it:
/// the business's saved look, found by the agent's id, then its name; an agent that isn't set up here keeps a look of
/// its own, so it is always the same face.
/// </summary>
public static class AgentLookup
{
    public static string? Look(string? id, string? name, IEnumerable<AiAgent> agents)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var list = agents.ToList();
        var agent = list.FirstOrDefault(a => id is not null && a.Id == id)
            ?? list.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        return agent?.Mascot ?? AgentBuilderCopy.Mascots[name.Sum(c => c) % AgentBuilderCopy.Mascots.Count];
    }
}
