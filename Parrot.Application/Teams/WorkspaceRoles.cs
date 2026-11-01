using static Parrot.Application.Teams.WorkspacePermissions;

namespace Parrot.Application.Teams;

/// <summary>
/// A built-in role: the same in every business and never changed by an owner.
/// <paramref name="Id"/> is what grants store; it never changes once used.
/// </summary>
public sealed record RoleInfo(string Id, string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// The built-in roles. Owners cannot change them; to adjust one, an owner
/// duplicates it into a role of their own (<see cref="Domain.Entities.OwnerRole"/>).
/// </summary>
public static class WorkspaceRoles
{
    /// <summary>Not a role anyone is given: the person who owns the workspace.</summary>
    public const string Owner = "Owner";

    public const string ConversationManager = "Conversation Manager";
    public const string AutomationManager = "Automation Manager";
    public const string ChannelManager = "Channel Manager";
    public const string CatalogManager = "Catalog Manager";
    public const string Analyst = "Analyst";

    public static readonly IReadOnlyList<RoleInfo> All =
    [
        new("conversation-manager", ConversationManager, [ConversationsView, ConversationsTakeOver, ConversationsReply, ConversationsClose, ConversationsEscalate, ConversationsLogs]),
        new("automation-manager", AutomationManager, [PlaybooksCreate, PlaybooksEdit, PlaybooksDelete, PlaybooksPublish, AgentsCreate, AgentsEdit]),
        new("channel-manager", ChannelManager, [ChannelsConnect, ChannelsDisconnect]),
        new("catalog-manager", CatalogManager, [CatalogView, CatalogCategories, CatalogAddItems, CatalogEditItems, CatalogDeleteItems, CatalogAvailability]),
        new("analyst", Analyst, [AnalyticsDashboard, AnalyticsQa, AnalyticsSummaries, AnalyticsTasks]),
    ];

    public static RoleInfo? FindById(string? id) => All.FirstOrDefault(r => r.Id == id);

    public static RoleInfo? FindByName(string name) => All.FirstOrDefault(r => r.Name == name);
}
