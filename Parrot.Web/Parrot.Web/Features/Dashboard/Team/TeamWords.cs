using System.Globalization;

namespace Parrot.Web.Features.Dashboard.Team;

/// <summary>
/// What the Team screens call things. The built-in roles go by job titles a seller would give the person
/// ("Customer service", not "Conversation Manager"), with one line on what that means (designer, 5 November 2026);
/// a page they open keeps the sidebar's name ("Products &amp; Services manager"). Their ids and permissions are the
/// API's; only the names shown here are the frontend's, until the backend adopts them (the invite email still uses
/// its own: team-backend-requests). A role of the owner's own keeps the name they gave it.
/// </summary>
public static class TeamWords
{
    private sealed record Words(string Name, string Does);

    // In the order the Roles list shows them: what most sellers hand over first.
    private static readonly (string Id, Words Words)[] BuiltIn =
    [
        ("conversation-manager", new("Customer service", "Replies to customers, takes chats over from your agents and handles tasks.")),
        ("catalog-manager", new("Products & Services manager", "Keeps products, prices and what's available up to date.")),
        ("automation-manager", new("AI agent manager", "Sets up your AI agents and what they do.")),
        ("channel-manager", new("Channel manager", "Connects and disconnects the business's channels.")),
        ("analyst", new("Analyst", "Sees how chats, agents and tasks are going in Insights.")),
    ];

    // Permissions grouped by the part of yára they open (designer, 8 November 2026: "list the permissions based on the
    // category"), named as the sidebar names them and in its order. The API gives one flat list; a key's first part
    // ("catalog" in "catalog.items.add") says where it belongs. Anything new and unknown still shows, under Other.
    private static readonly (string Title, string[] Prefixes)[] Areas =
    [
        ("Inbox", ["conversations"]),
        ("Products & Services", ["catalog"]),
        ("Channels", ["channels"]),
        ("AI agents", ["agents", "playbooks"]),
        ("Insights", ["analytics"]),
    ];

    /// <summary>A part of yára and the permissions for it.</summary>
    public sealed record PermissionGroup(string Title, IReadOnlyList<TeamOption> Permissions);

    /// <summary>The permissions in groups, in the sidebar's order, each keeping the API's own order inside it.</summary>
    public static IReadOnlyList<PermissionGroup> PermissionGroups(IEnumerable<TeamOption> permissions)
    {
        var list = permissions.ToList();
        static string Prefix(TeamOption option) => option.Key.Split('.')[0];
        var groups = Areas.Select(area => new PermissionGroup(area.Title, [.. list.Where(option => area.Prefixes.Contains(Prefix(option)))]))
            .Where(group => group.Permissions.Count > 0).ToList();
        var known = Areas.SelectMany(area => area.Prefixes).ToHashSet();
        if (list.Where(option => !known.Contains(Prefix(option))).ToList() is { Count: > 0 } other) groups.Add(new("Other", other));
        return groups;
    }

    /// <summary>
    /// What a role opens, by area rather than permission by permission ("Works in Inbox and Products &amp; Services."):
    /// a built-in's own line, or for an owner's own role the areas it has anything in.
    /// </summary>
    public static string RoleAreas(TeamRole role, IEnumerable<TeamOption> catalog)
    {
        if (RoleDoes(role.Id) is { } does) return does;
        var areas = PermissionGroups(catalog).Where(group => group.Permissions.Any(p => role.Permissions.Contains(p.Key))).Select(group => group.Title).ToList();
        return areas.Count switch
        {
            0 => "No permissions yet.",
            1 => $"Works in {areas[0]}.",
            _ => $"Works in {string.Join(", ", areas[..^1])} and {areas[^1]}.",
        };
    }

    /// <summary>"6 permissions", "1 permission", or "No permissions": a role's size, as role lists elsewhere show it.</summary>
    public static string PermissionCount(int count) => count switch { 0 => "No permissions", 1 => "1 permission", _ => $"{count} permissions" };

    public static string RoleName(string roleId, string apiName) =>
        BuiltIn.FirstOrDefault(entry => entry.Id == roleId).Words?.Name ?? apiName;

    public static string RoleName(TeamRole role) => RoleName(role.Id, role.Name);

    // The API's own names for the built-in roles (WorkspaceRoles), for where it gives a role by name only: the
    // workspace list says "Conversation Manager" where the Team page says "Customer service".
    private static readonly Dictionary<string, string> ApiNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Conversation Manager"] = "conversation-manager",
        ["Catalog Manager"] = "catalog-manager",
        ["Automation Manager"] = "automation-manager",
        ["Channel Manager"] = "channel-manager",
        ["Analyst"] = "analyst",
    };

    /// <summary>A role the API names without its id, in the Team page's words; an owner's own role keeps its name.</summary>
    public static string RoleNameFromApi(string apiName) =>
        ApiNames.TryGetValue(apiName.Trim(), out var id) ? RoleName(id, apiName) : apiName;

    public static string RoleName(TeamGrant grant) => RoleName(grant.RoleId, grant.RoleName);

    /// <summary>One line on what a built-in role lets someone do; null for the owner's own roles.</summary>
    public static string? RoleDoes(string roleId) => BuiltIn.FirstOrDefault(entry => entry.Id == roleId).Words?.Does;

    /// <summary>Built-in roles first, in the order above, then the owner's own by name.</summary>
    public static IReadOnlyList<TeamRole> Ordered(IEnumerable<TeamRole> roles)
    {
        var order = BuiltIn.Select((entry, index) => (entry.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        return [.. roles.OrderBy(role => role.IsCustom ? 1 : 0)
            .ThenBy(role => order.TryGetValue(role.Id, out var index) ? index : int.MaxValue)
            .ThenBy(role => role.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>What a person is, on one line: their role, or how many when it differs by workspace.</summary>
    public static string RoleLine(IReadOnlyList<TeamGrant> grants) =>
        grants.Count == 0 ? "No access"
        : grants.Select(grant => grant.RoleId).Distinct().Count() == 1 ? RoleName(grants[0])
        : $"{grants.Select(grant => grant.RoleId).Distinct().Count()} roles";

    /// <summary>Where they work: "All workspaces", one workspace's name, or how many.</summary>
    public static string Where(IReadOnlyList<TeamGrant> grants, IReadOnlyList<TeamWorkspace> workspaces) =>
        grants.Count == 0 ? "No workspace"
        : grants.Any(grant => grant.WorkspaceId is null) ? "All workspaces"
        : grants.Count == 1 ? workspaces.FirstOrDefault(w => w.Id == grants[0].WorkspaceId)?.Name ?? "A workspace"
        : $"{grants.Count} workspaces";

    /// <summary>The channels they see, in the API's own order (as the invite form lists them); empty for every
    /// channel; null when it differs from one workspace to another.</summary>
    public static IReadOnlyList<string>? Channels(IReadOnlyList<TeamGrant> grants, IReadOnlyList<TeamOption> options)
    {
        if (grants.Count == 0) return [];
        var first = grants[0].Channels;
        if (grants.Any(grant => !grant.Channels.ToHashSet().SetEquals(first))) return null;
        return [.. options.Where(option => first.Contains(option.Key)).Select(option => option.Key), .. first.Where(key => options.All(option => option.Key != key))];
    }

    /// <summary>"Invited · 5 days left", or "Invited · last day" on its final day.</summary>
    public static string Invited(DateTime expiresAt, DateTime now)
    {
        var days = (int)Math.Ceiling((expiresAt - now).TotalDays);
        return days <= 1 ? "Invited · last day" : $"Invited · {days} days left";
    }

    /// <summary>"18 Oct", or "18 Oct 2025" from another year.</summary>
    public static string Date(DateTime at, DateTime now) =>
        at.ToString(at.Year == now.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
}
