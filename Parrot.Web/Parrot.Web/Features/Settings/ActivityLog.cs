using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Features.Settings;

/// <summary>
/// The place in yára an event belongs to: one area per page, named as the app names it (designer, 9 November 2026:
/// never two places grouped as one). <see cref="Other"/> is for a type the frontend doesn't know yet; it isn't offered
/// as a filter.
/// </summary>
public enum ActivityArea { Agents, Products, Tools, Channels, Customers, Security, Business, Team, Billing, Other }

/// <summary>Who did it: a person on the team, an AI agent, or yára itself (a connection lost, a link expiring).</summary>
public enum ActorKind { Person, Agent, System }

/// <param name="Detail">A short second line: a person's role, or "AI agent".</param>
public sealed record ActivityActor(ActorKind Kind, string Id, string Name, string? Detail = null);

/// <summary>One field that changed, before and after (null where there was nothing).</summary>
public sealed record ActivityChange(string Field, string? From, string? To);

/// <summary>
/// One thing that changed in the business (designer, 9 November 2026: an audit log, "who changed what, and when").
/// The action is a fixed type (<see cref="Action"/>, e.g. "team.invited") and its subject; the sentence a person
/// reads is the frontend's own template for that type (<see cref="ActivityWords"/>), so every event of a kind reads
/// alike. Read-only: nothing here is ever edited or removed.
/// </summary>
/// <param name="Subject">What it was done to: a person, a role, a channel, an item.</param>
/// <param name="Detail">A word the sentence needs: the role someone was invited as, how many items.</param>
/// <param name="Link">Where the thing lives in yára now, to open it (null once it's gone).</param>
/// <param name="Device">For sign-ins and security changes: the browser and system.</param>
/// <param name="Location">For sign-ins and security changes: roughly where, from the address.</param>
public sealed record ActivityEvent(
    string Id, DateTime OccurredAt, ActivityActor Actor, string Action,
    string? Subject = null, string? Detail = null, IReadOnlyList<ActivityChange>? Changes = null,
    string? Link = null, string? Device = null, string? Location = null);

/// <summary>What the list is narrowed to. <see cref="Days"/>: 1 for today, 7, 30, or null for any time.</summary>
public sealed record ActivityQuery(string? Search = null, string? ActorId = null, ActivityArea? Area = null, int? Days = null, int Offset = 0, int Limit = 15);

/// <summary>One page of events, newest first; the total for the query; and everyone who appears, for the Who filter.</summary>
public sealed record ActivityPage(IReadOnlyList<ActivityEvent> Items, int Total, IReadOnlyList<ActivityActor> Actors);

/// <summary>The business's activity log. The API doesn't keep one yet (activity-backend-requests).</summary>
public interface IActivityLog
{
    /// <summary>False until the API records events: the page says the log is coming instead of looking empty.</summary>
    bool Available { get; }
    Task<ActivityPage> ListAsync(ActivityQuery query, CancellationToken ct = default);

    /// <summary>Every event the query matches (not just a page), as CSV, for the download.</summary>
    Task<string> ExportCsvAsync(ActivityQuery query, CancellationToken ct = default);
}

/// <summary>No activity log in the API yet: nothing is listed or claimed.</summary>
public sealed class UnavailableActivityLog : IActivityLog
{
    public bool Available => false;
    public Task<ActivityPage> ListAsync(ActivityQuery query, CancellationToken ct = default) => Task.FromResult(new ActivityPage([], 0, []));
    public Task<string> ExportCsvAsync(ActivityQuery query, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Times and links as the activity log shows them.</summary>
public static class ActivityTime
{
    /// <summary>"9 Nov 2026, 2:14pm", in the person's own time zone.</summary>
    public static string Exact(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var hour = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        return $"{local.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}, {hour}:{local.Minute:00}{(local.Hour < 12 ? "am" : "pm")}";
    }

    /// <summary>The button that opens the thing an event is about, named for where it goes.</summary>
    public static string LinkLabel(string link) => link switch
    {
        _ when link.StartsWith("/dashboard/settings/team", StringComparison.Ordinal) => "Open Team",
        _ when link.StartsWith("/dashboard/settings/security", StringComparison.Ordinal) => "Open Sign-in & security",
        _ when link.StartsWith("/dashboard/settings/business", StringComparison.Ordinal) => "Open Business profile",
        _ when link.StartsWith("/dashboard/settings/billing", StringComparison.Ordinal) => "Open Plan & billing",
        _ when link.StartsWith("/dashboard/channels", StringComparison.Ordinal) => "Open Channels",
        _ when link.StartsWith("/dashboard/tools", StringComparison.Ordinal) => "Open Tools",
        _ when link.StartsWith("/dashboard/ai-operations", StringComparison.Ordinal) => "Open Agents",
        _ when link.StartsWith("/dashboard/products", StringComparison.Ordinal) => "Open Products & Services",
        _ when link.StartsWith("/dashboard/customers", StringComparison.Ordinal) => "Open Customers",
        _ when link.StartsWith("/dashboard/inbox", StringComparison.Ordinal) => "Open Inbox",
        _ => "Open",
    };
}

/// <summary>
/// What the activity log calls things: the area each type belongs to, and the one sentence each type reads as. Fixed
/// templates (as notifications' headlines are): the subject and a detail are filled in, nothing else varies. A type
/// the frontend doesn't know yet still shows, in plain words, rather than disappearing.
/// </summary>
public static class ActivityWords
{
    /// <summary>The Area filter's choices, in the app's own order: the rail's pages, then Settings' tabs.</summary>
    public static IReadOnlyList<ActivityArea> Areas { get; } =
    [
        ActivityArea.Agents, ActivityArea.Products, ActivityArea.Tools, ActivityArea.Channels, ActivityArea.Customers,
        ActivityArea.Security, ActivityArea.Business, ActivityArea.Team, ActivityArea.Billing,
    ];

    /// <summary>The page's own name, as the rail (NavMap) and Settings (SettingsSections) call it.</summary>
    public static string AreaName(ActivityArea area) => area switch
    {
        ActivityArea.Agents => "Agents",
        ActivityArea.Products => "Products & Services",
        ActivityArea.Tools => "Tools",
        ActivityArea.Channels => "Channels",
        ActivityArea.Customers => "Customers",
        ActivityArea.Security => "Sign-in & security",
        ActivityArea.Business => "Business profile",
        ActivityArea.Team => "Team",
        ActivityArea.Billing => "Plan & billing",
        _ => "Other",
    };

    /// <summary>The page's own icon, as the rail and Settings draw it.</summary>
    public static IconName AreaIcon(ActivityArea area) => area switch
    {
        ActivityArea.Agents => IconName.RiRobot3Fill,
        ActivityArea.Products => IconName.RiShoppingBag3Fill,
        ActivityArea.Tools => IconName.RiExchangeBoxFill,
        ActivityArea.Channels => IconName.RiLinksFill,
        ActivityArea.Customers => IconName.RiContactsBook3Fill,
        ActivityArea.Security => IconName.RiLock2Fill,
        ActivityArea.Business => IconName.RiStore2Fill,
        ActivityArea.Team => IconName.RiTeamFill,
        ActivityArea.Billing => IconName.RiBankCardFill,
        _ => IconName.RiFileList3Fill,
    };

    /// <summary>The area a type belongs to, from its first part ("team" in "team.invited").</summary>
    public static ActivityArea AreaOf(string action) => action.Split('.')[0] switch
    {
        "agents" => ActivityArea.Agents,
        "catalog" => ActivityArea.Products,
        "tools" => ActivityArea.Tools,
        "channels" => ActivityArea.Channels,
        "customers" => ActivityArea.Customers,
        "security" => ActivityArea.Security,
        "business" => ActivityArea.Business,
        "team" => ActivityArea.Team,
        "billing" => ActivityArea.Billing,
        _ => ActivityArea.Other,
    };

    /// <summary>The sentence for an event, without who did it (the Who column says that).</summary>
    public static string Sentence(ActivityEvent e)
    {
        var s = e.Subject ?? "";
        var d = e.Detail ?? "";
        return e.Action switch
        {
            "security.signed_in" => "Signed in on a new device",
            "security.password_changed" => "Changed their password",
            "security.password_reset" => "Reset their password from an email link",
            "security.two_factor_on" => "Turned on two-factor authentication",
            "security.two_factor_off" => "Turned off two-factor authentication",
            "security.recovery_codes" => "Got new recovery codes",
            "security.two_factor_method_added" => d.Length > 0 ? $"Added {d} for two-factor authentication" : "Added a way to get sign-in codes",
            "security.two_factor_method_removed" => d.Length > 0 ? $"Removed {d} from two-factor authentication" : "Removed a way to get sign-in codes",
            "security.email_changed" => "Changed their sign-in email",
            "security.devices_signed_out" => "Signed out their other devices",
            "team.invited" => d.Length > 0 ? $"Invited {s} as {d}" : $"Invited {s}",
            "team.invite_resent" => $"Sent {s} their invite again",
            "team.invite_withdrawn" => $"Withdrew {s}'s invite",
            "team.joined" => d.Length > 0 ? $"Joined the team as {d}" : "Joined the team",
            "team.access_changed" => $"Changed {s}'s access",
            "team.removed" => $"Removed {s} from the team",
            "team.role_created" => $"Created the {s} role",
            "team.role_edited" => $"Edited the {s} role",
            "team.role_deleted" => $"Deleted the {s} role",
            "business.profile_changed" => "Updated the business profile",
            "business.logo_changed" => "Changed the business logo",
            "billing.plan_changed" => $"Changed the plan to {d}",
            "channels.connected" or "tools.connected" => $"Connected {s}",
            "channels.disconnected" or "tools.disconnected" => $"Disconnected {s}",
            "channels.connection_lost" => $"Lost the connection to {s}",
            "agents.created" => $"Created the AI agent {s}",
            "agents.edited" => $"Edited {s}",
            "agents.turned_on" => $"Turned on {s}",
            "agents.turned_off" => $"Turned off {s}",
            "agents.approval_allowed" => $"Allowed {s} to {d}",
            "agents.approval_declined" => $"Didn't allow {s} to {d}",
            "catalog.items_added" => $"Added {d} to Products & Services",
            "catalog.item_edited" => $"Edited {s}",
            "catalog.item_deleted" => $"Deleted {s}",
            "customers.imported" => $"Imported {d}",
            "customers.merged" => $"Merged {s}",
            "customers.exported" => "Downloaded the customer list",
            "customers.deleted" => $"Deleted {s}",
            _ => s.Length > 0 ? $"Changed {s}" : "Made a change",
        };
    }
}
