namespace Parrot.Application.Teams;

/// <summary>
/// One permission as the invite form lists it. <paramref name="Code"/> is how a
/// session token spells it, to keep the token small; codes never change once used.
/// </summary>
public sealed record PermissionInfo(string Key, string Label, string Code);

/// <summary>
/// Everything a member can be allowed to do in a workspace. The keys are what
/// a member's row stores and what <c>[RequirePermission]</c> checks; the labels
/// are what the invite form shows. Running the team and the workspace itself
/// are the owner's alone and are never granted.
/// </summary>
public static class WorkspacePermissions
{
    public const string ConversationsView = "conversations.view";
    public const string ConversationsTakeOver = "conversations.take-over";
    public const string ConversationsReply = "conversations.reply";
    public const string ConversationsClose = "conversations.close";
    public const string ConversationsEscalate = "conversations.escalate";
    public const string ConversationsLogs = "conversations.logs";

    public const string PlaybooksCreate = "playbooks.create";
    public const string PlaybooksEdit = "playbooks.edit";
    public const string PlaybooksDelete = "playbooks.delete";
    public const string PlaybooksPublish = "playbooks.publish";
    public const string AgentsCreate = "agents.create";
    public const string AgentsEdit = "agents.edit";

    public const string ChannelsConnect = "channels.connect";
    public const string ChannelsDisconnect = "channels.disconnect";

    public const string CatalogView = "catalog.view";
    public const string CatalogCategories = "catalog.categories";
    public const string CatalogAddItems = "catalog.items.add";
    public const string CatalogEditItems = "catalog.items.edit";
    public const string CatalogDeleteItems = "catalog.items.delete";
    public const string CatalogAvailability = "catalog.availability";

    public const string AnalyticsDashboard = "analytics.dashboard";
    public const string AnalyticsQa = "analytics.qa";
    public const string AnalyticsSummaries = "analytics.summaries";
    public const string AnalyticsTasks = "analytics.tasks";

    /// <summary>Invite, change and remove members. Owner only.</summary>
    public const string TeamManage = "team.manage";

    /// <summary>Rename the workspace, change its logo, run business setup. Owner only.</summary>
    public const string WorkspaceManage = "workspace.manage";

    /// <summary>Every permission a member can be given, in the order the form lists them.</summary>
    public static readonly IReadOnlyList<PermissionInfo> Grantable =
    [
        new(ConversationsView, "View conversations", "cv"),
        new(ConversationsTakeOver, "Take control of conversation", "ct"),
        new(ConversationsReply, "Send manual replies", "cr"),
        new(ConversationsClose, "Close conversation", "cc"),
        new(ConversationsEscalate, "Escalate conversation", "ce"),
        new(ConversationsLogs, "View conversation logs", "cl"),
        new(PlaybooksCreate, "Create playbook", "pc"),
        new(PlaybooksEdit, "Edit playbook", "pe"),
        new(PlaybooksDelete, "Delete playbook", "pd"),
        new(PlaybooksPublish, "Publish playbook", "pp"),
        new(AgentsCreate, "Create AI agent", "ac"),
        new(AgentsEdit, "Edit AI agent", "ae"),
        new(ChannelsConnect, "Connect channel", "hc"),
        new(ChannelsDisconnect, "Disconnect channel", "hd"),
        new(CatalogView, "View catalog", "kv"),
        new(CatalogCategories, "Manage categories", "kg"),
        new(CatalogAddItems, "Add items", "ka"),
        new(CatalogEditItems, "Edit items", "ke"),
        new(CatalogDeleteItems, "Delete item", "kd"),
        new(CatalogAvailability, "Update availability", "kk"),
        new(AnalyticsDashboard, "View dashboard metrics", "ad"),
        new(AnalyticsQa, "View QA analysis", "aq"),
        new(AnalyticsSummaries, "View conversation summaries", "as"),
        new(AnalyticsTasks, "View task reports", "at"),
    ];

    private static readonly HashSet<string> GrantableKeys = Grantable.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    public static bool IsGrantable(string key) => GrantableKeys.Contains(key);

    /// <summary>Every permission the access allows, by key: all of them, the owner-only two included, for the owner.</summary>
    public static IReadOnlyList<string> Effective(WorkspaceAccess access) =>
        access.IsOwner
            ? [.. Grantable.Select(p => p.Key), TeamManage, WorkspaceManage]
            : [.. Grantable.Select(p => p.Key).Where(access.Can)];

    /// <summary>What a session token carries for an owner, who may do everything.</summary>
    public const string AllCode = "*";

    /// <summary>
    /// The permissions as a session token carries them: their codes, space
    /// separated, e.g. "cv cr cl"; "*" for the owner.
    /// </summary>
    public static string Abbreviate(WorkspaceAccess access) =>
        access.IsOwner
            ? AllCode
            : string.Join(' ', Grantable.Where(p => access.Can(p.Key)).Select(p => p.Code));

    /// <summary>The keys a token's codes stand for. Unknown codes are skipped.</summary>
    public static IReadOnlyList<string> Expand(string? codes) =>
        string.IsNullOrWhiteSpace(codes)
            ? []
            : codes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(code => Grantable.FirstOrDefault(p => p.Code == code)?.Key)
                .OfType<string>()
                .ToList();

    /// <summary>
    /// The catalog service's scopes for a set of permissions. It reads them from
    /// the token alone, so a change reaches the catalog at the member's next token.
    /// </summary>
    public static string CatalogScopes(WorkspaceAccess access)
    {
        if (access.IsOwner)
        {
            return "catalog:read catalog:write catalog:publish catalog:admin";
        }

        List<string> scopes = [];
        if (access.Can(CatalogView))
        {
            scopes.Add("catalog:read");
        }

        if (access.Can(CatalogCategories) || access.Can(CatalogAddItems) || access.Can(CatalogEditItems)
            || access.Can(CatalogDeleteItems) || access.Can(CatalogAvailability))
        {
            scopes.Add("catalog:write");
        }

        return string.Join(' ', scopes);
    }
}
