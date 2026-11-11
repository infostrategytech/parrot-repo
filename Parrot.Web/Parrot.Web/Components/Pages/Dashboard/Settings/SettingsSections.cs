using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Layout;

namespace Parrot.Web.Components.Pages.Dashboard.Settings;

/// <summary>One section of Settings: its address segment, name, line under the title and icon. Rule: the access
/// rule (AccessRules) that opens it; null for the person's own settings, open to everyone.</summary>
public sealed record SettingsSection(string Id, string Label, string Description, IconName Glyph, string? Rule = null);

/// <summary>
/// The sections, in the order the list shows them (designer, 1 November 2026): You, then Business for owners and
/// admins. Shared by the Settings layout's list and the page that shows the current one.
/// </summary>
public static class SettingsSections
{
    public const string Root = "/dashboard/settings";

    public static IReadOnlyList<SettingsSection> You { get; } =
    [
        new("profile", "Profile", "Your name, photo, phone number and email.", IconName.RiUser3Fill),
        new("appearance", "Appearance", "How yára looks for you.", IconName.RiSunFill),
        new("notifications", "Notifications", "Choose how and when you'd like to hear from yára.", IconName.RiNotification3Fill),
        new("security", "Sign-in & security", "Keep your account safe.", IconName.RiLock2Fill),
    ];

    public static IReadOnlyList<SettingsSection> Business { get; } =
    [
        new("business", "Business profile", "What customers and your agents know about the business.", IconName.RiStore2Fill, AccessRules.Business),
        new("team", "Team", "The people who help run the business in yára.", IconName.RiTeamFill, AccessRules.Team),
        new("billing", "Plan & billing", "Your plan, usage and invoices.", IconName.RiBankCardFill, AccessRules.Business),
        new("activity", "Activity log", "Who changed what, and when.", IconName.RiFileList3Fill, AccessRules.Business),
    ];

    /// <summary>The list, with only the sections the person may open (the backend team's access rules): a team
    /// member sees You; the owner sees Business too. A group with nothing left in it goes.</summary>
    public static IReadOnlyList<SectionNavGroup> GroupsFor(Func<string, bool> allows) =>
    [
        .. new[] { ("You", You), ("Business", Business) }
            .Select(group => new SectionNavGroup(group.Item1, [.. group.Item2.Where(section => Opens(section, allows)).Select(Item)]))
            .Where(group => group.Items.Count > 0),
    ];

    /// <summary>The section an address shows; Profile when it names none, one that doesn't exist, or one the
    /// person may not open (even typed in).</summary>
    public static SettingsSection For(string? id, Func<string, bool> allows) =>
        You.Concat(Business).FirstOrDefault(section => section.Id == id?.ToLowerInvariant() && Opens(section, allows)) ?? You[0];

    private static bool Opens(SettingsSection section, Func<string, bool> allows) => section.Rule is null || allows(section.Rule);

    /// <summary>The section named in a full address, or null for Settings itself (on a phone, the list).</summary>
    public static string? IdIn(string uri)
    {
        var path = new Uri(uri).AbsolutePath.TrimEnd('/');
        return path.StartsWith(Root + "/", StringComparison.OrdinalIgnoreCase) ? path[(Root.Length + 1)..].Split('/')[0].ToLowerInvariant() : null;
    }

    public static bool IsSettings(string uri)
    {
        var path = new Uri(uri).AbsolutePath.TrimEnd('/');
        return path.Equals(Root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static SectionNavItem Item(SettingsSection section) => new(section.Id, section.Label, $"{Root}/{section.Id}", section.Glyph);
}

/// <summary>Where "Back to app" goes: the last page outside Settings, kept for the session.</summary>
public sealed class SettingsReturn
{
    public string Url { get; private set; } = "/dashboard";
    public void Remember(string uri)
    {
        if (SettingsSections.IsSettings(uri)) return;
        var address = new Uri(uri);
        Url = address.PathAndQuery;
    }
}
