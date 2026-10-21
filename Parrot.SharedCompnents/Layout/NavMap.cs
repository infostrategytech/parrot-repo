using Parrot.SharedComponents.DesignSystem;

namespace Parrot.SharedComponents.Layout;

/// <param name="Path">The route. Null means the item renders but does not navigate.</param>
/// <param name="Label">What the rail reads, and what the page is called everywhere else.</param>
/// <param name="Glyph">Its icon in the rail.</param>
public sealed record NavEntry(
    string? Path,
    string Label,
    IconName Glyph,
    string? CompactLabel = null);

/// <summary>
/// The navigation, as data. One table drives the rail, the active state and
/// the per-page shell class.
///
/// The rows, their order and their glyphs are Aurora's. Overview keeps the
/// existing dashboard route, while standalone modules use their own canonical
/// routes; AI Operations is not a dashboard child. Customers is Contacts and Insights is QA &amp; Conversation
/// Insight. The two rows with a null Path — Campaigns and Help Centre — are
/// drawn in Aurora with no screen behind them, and there is none in Parrot
/// either, so they stay marked rather than feeling broken.
/// </summary>
public static class NavMap
{
    public static readonly IReadOnlyList<NavEntry> Primary =
    [
        new("/dashboard", "Overview", IconName.RiHome5Fill, "Overview"),
        new("/dashboard/conversations", "Inbox", IconName.RiInbox2Fill, "Inbox"),
        new("/dashboard/contacts", "Customers", IconName.RiContactsBook3Fill, "Customers"),
        new("/dashboard/catalog", "Products & Services", IconName.RiShoppingBag3Fill, "Products"),
        new("/dashboard/tasks", "Tasks", IconName.RiClipboardFill, "Tasks"),
        new("/dashboard/channels", "Channels", IconName.RiLinksFill, "Channels"),
        new("/ai-operations", "AI Operations", IconName.RiRobot3Fill, "AI"),
        new(null, "Campaigns", IconName.RiMegaphoneFill, "Campaigns"),
        new("/dashboard/qa-insights", "Insights", IconName.RiBarChartBoxFill, "Insights")
    ];

    public static readonly IReadOnlyList<NavEntry> Footer =
    [
        new("/dashboard/knowledge", "Knowledge", IconName.RiBookOpenFill, "Knowledge"),
        new(null, "Help Centre", IconName.RiQuestionFill, "Help"),
        new("/dashboard/settings", "Settings", IconName.RiSettings4Fill, "Settings")
    ];

    public static IEnumerable<NavEntry> All => Primary.Concat(Footer);

    /// <summary>
    /// The entry a URL belongs to, by longest matching path — so
    /// /dashboard/tasks/OM-12031 still highlights Tasks, and
    /// /dashboard/catalog is Products rather than Overview.
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
