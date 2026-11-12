using Parrot.SharedComponents.DesignSystem;

namespace Parrot.SharedComponents.Layout;

/// <param name="Path">The route. Null means the item renders but does not navigate.</param>
/// <param name="Label">What the rail reads, and what the page is called everywhere else.</param>
/// <param name="Glyph">Its icon in the rail.</param>
/// <param name="AnyPermission">
/// Permission keys, any one of which opens the page for a team member. Null is
/// everyone. The owner can open every page. The API checks the same rules on
/// every request; this only keeps the rail to what the person can use.
/// </param>
public sealed record NavEntry(
    string? Path,
    string Label,
    IconName Glyph,
    string? CompactLabel = null,
    IReadOnlyList<string>? AnyPermission = null);

/// <summary>
/// The navigation, as data. One table drives the rail, the active state and
/// the per-page shell class.
///
/// The rows, their order and their glyphs are Aurora's. Overview keeps the
/// existing dashboard route, while standalone modules use their own canonical
/// routes; AI Operations is not a dashboard child. Customers is Contacts and Insights is QA &amp; Conversation
/// Insight. Tools replaces the unused Campaigns item. Help Centre moved into the account menu (3 November 2026),
/// unavailable until it has a screen, and Settings followed it (5 November 2026), when Notifications moved under Inbox.
/// </summary>
public static class NavMap
{
    // Regrouped (designer, 11 November 2026), each group a quiet divider in the rail: your daily work (Inbox and Tasks
    // with counts, and Customers, looked up while you work them); your agents and what they know and sell; what they
    // connect to (set up once, visited rarely); and how it's going. Settings and Notifications are at the foot.
    public static readonly IReadOnlyList<NavEntry> Primary =
    [
        new("/dashboard", "Overview", IconName.RiHome5Fill, "Overview"),
        new("/dashboard/inbox", "Inbox", IconName.RiInbox2Fill, "Inbox", AccessRules.For(AccessRules.Inbox)),
        new("/dashboard/tasks", "Tasks", IconName.RiClipboardFill, "Tasks", AccessRules.For(AccessRules.Tasks)),
        new("/dashboard/customers", "Customers", IconName.RiContactsBook3Fill, "Customers", AccessRules.For(AccessRules.Customers)),
        // "Agents", the vendor's word, not "AI Operations" (designer, 11 November 2026); the address stays.
        new("/dashboard/ai-operations", "Agents", IconName.RiRobot3Fill, "Agents", AccessRules.For(AccessRules.Agents)),
        new("/dashboard/knowledge", "Knowledge", IconName.RiBookOpenFill, "Knowledge", AccessRules.For(AccessRules.Knowledge)),
        new("/dashboard/products", "Products & Services", IconName.RiShoppingBag3Fill, "Products", AccessRules.For(AccessRules.Catalog)),
        new("/dashboard/channels", "Channels", IconName.RiLinksFill, "Channels", AccessRules.For(AccessRules.Channels)),
        // The apps the business connects for its agents; managing them is managing the business.
        new("/dashboard/tools", "Tools", IconName.RiExchangeBoxFill, "Tools", AccessRules.For(AccessRules.Business)),
        new("/dashboard/insights", "Insights", IconName.RiBarChartBoxFill, "Insights", AccessRules.For(AccessRules.Insights))
    ];

    // Where a new group starts in the rail: a divider is drawn before it (when something above it is shown).
    private static readonly HashSet<string> GroupStarts = ["/dashboard/ai-operations", "/dashboard/channels", "/dashboard/insights"];

    public static bool StartsGroup(NavEntry entry) => entry.Path is { } path && GroupStarts.Contains(path);

    // Rendered by the host as a popover trigger at the rail's foot, beside the account card (designer, 11 November 2026),
    // not one of these rows.
    // It still owns its page route, so that route must never fall back to Overview. Everyone has notifications.
    public static readonly NavEntry Notifications = new("/dashboard/notifications", "Notifications", IconName.RiNotification3Fill, "Notifications");

    // Opened from the account menu, beside Your profile and Help Centre (designer, 5 November 2026), not a rail row.
    // Like Notifications, it keeps its route here so it never falls back to Overview.
    public static readonly NavEntry Settings = new("/dashboard/settings", "Settings", IconName.RiSettings4Fill, "Settings");

    public static IEnumerable<NavEntry> All => Primary.Append(Notifications).Append(Settings);

    /// <summary>
    /// The entry a URL belongs to, by longest matching path — so
    /// /dashboard/tasks/OM-12031 still highlights Tasks, and
    /// /dashboard/products is Products rather than Overview.
    /// </summary>
    public static NavEntry? Match(string path)
    {
        var normalised = "/" + path.Trim('/');

        return All
            .Where(entry => entry.Path is not null)
            .Where(entry => string.Equals(normalised, entry.Path, StringComparison.OrdinalIgnoreCase) ||
                            normalised.StartsWith(entry.Path + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Path!.Length)
            .FirstOrDefault();
    }
}
