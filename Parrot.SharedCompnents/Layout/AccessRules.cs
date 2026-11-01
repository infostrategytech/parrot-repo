namespace Parrot.SharedComponents.Layout;

/// <summary>
/// Which permissions open each part of the dashboard, in one place. The rail
/// (<see cref="NavMap"/>), the pages (RequiresAccess on each page) and the
/// buttons that lead to them all read these, so what a person can see always
/// matches what they can open. A rule passes when the person holds any one of
/// its permissions; the owner passes every rule. The API enforces the same
/// rules on every request; this only keeps the screens to what can be used.
/// </summary>
public static class AccessRules
{
    public const string Inbox = "inbox";
    public const string Customers = "customers";
    public const string Catalog = "catalog";
    public const string Tasks = "tasks";
    public const string Channels = "channels";
    public const string Agents = "agents";
    public const string AgentCreate = "agent-create";
    public const string AgentEdit = "agent-edit";
    public const string Insights = "insights";
    public const string Knowledge = "knowledge";
    public const string Team = "team";

    /// <summary>The business itself: its name and logo, setup, automation and billing. Owner only.</summary>
    public const string Business = "business";

    private static readonly Dictionary<string, string[]> Rules = new()
    {
        [Inbox] = ["conversations.view"],
        [Customers] = ["conversations.view"],
        [Catalog] = ["catalog.view", "catalog.categories", "catalog.items.add", "catalog.items.edit", "catalog.items.delete", "catalog.availability"],
        // The team's customer work queue: whoever works conversations, and the Analyst's task reports.
        [Tasks] = ["conversations.view", "conversations.take-over", "analytics.tasks"],
        [Channels] = ["channels.connect", "channels.disconnect"],
        // Seeing the agents, as the API allows it: whoever builds them, works the
        // conversations they answer, or reads their figures.
        [Agents] = ["agents.create", "agents.edit", "playbooks.create", "playbooks.edit", "playbooks.delete", "playbooks.publish",
            "conversations.view", "conversations.take-over", "analytics.dashboard"],
        [AgentCreate] = ["agents.create"],
        [AgentEdit] = ["agents.edit", "playbooks.edit"],
        [Insights] = ["analytics.qa", "analytics.summaries"],
        [Knowledge] = ["agents.create", "agents.edit", "workspace.manage"],
        [Team] = ["team.manage"],
        [Business] = ["workspace.manage"],
    };

    /// <summary>The permissions, any one of which passes the rule.</summary>
    public static IReadOnlyList<string> For(string rule) =>
        Rules.TryGetValue(rule, out string[]? permissions) ? permissions : throw new ArgumentException($"No access rule \"{rule}\".", nameof(rule));

    /// <param name="isOwner">The owner passes every rule.</param>
    /// <param name="can">Whether the person holds one permission.</param>
    public static bool Allows(string rule, bool isOwner, Func<string, bool> can) => isOwner || For(rule).Any(can);
}
