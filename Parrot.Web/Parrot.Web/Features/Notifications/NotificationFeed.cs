using Parrot.Web.Features.Settings;

namespace Parrot.Web.Features.Notifications;

/// <summary>
/// An Action needs someone to do something (take a chat, approve a link, reconnect): it stays in "Needs you" until
/// it's done, wherever it's done, then clears itself; reading it doesn't. An Update is worth knowing (a booking
/// made, a payment received) and is done once seen. Summaries are email only, never here (designer, 3 November 2026).
/// </summary>
public enum NotificationKind { Action, Update }

/// <summary>What the line's mark shows. Waiting (a clock) is something waiting on you, Late is overdue, Problem is
/// something broken; a tick is kept for what's done (designer, 3 November 2026).</summary>
public enum NotificationMark { Chat, Waiting, Task, Late, Booking, Payment, Problem, Agent, Team, Security }
public enum NotificationResolution { Completed, Answered, Approved, Declined, Cancelled, Withdrawn, Repaired, Unavailable, WaitingOnCustomer }

/// <summary>
/// One thing the business should know about: what happened, in plain words; its group (the same as Settings ›
/// Notifications); action or update; Place, where it opens, said on its line ("Inbox"). Unread until seen. An action is done when ResolvedAt is set,
/// with who or what did it (ResolvedNote); the API sets that wherever it's done.
/// </summary>
public sealed record AppNotification(string Id, AlertGroup Group, NotificationKind Kind, NotificationMark Mark, string Text, string Place,
    DateTimeOffset At, bool Unread, string? Href, DateTimeOffset? ResolvedAt = null, string? ResolvedNote = null,
    NotificationType Type = NotificationType.Legacy, string? AgentId = null, string? AgentName = null, string? Detail = null,
    string? SubjectId = null, string? WorkspaceId = null, string? RecipientId = null, NotificationResolution Resolution = NotificationResolution.Completed,
    int? PendingCount = null)
{
    public bool NeedsYou => Kind == NotificationKind.Action && ResolvedAt is null;
}

/// <summary>The bell's notifications for the person signed in. The API decides what reaches whom (assigned person
/// first, then whoever's role can act; personal security to the affected account, billing to authorised managers), from Settings › Notifications; this
/// only shows them. New messages never come here: Inbox's count is for those.</summary>
public interface INotificationFeed
{
    bool Available { get; }
    event Action? Changed;
    Task<IReadOnlyList<AppNotification>> ReadAsync(CancellationToken ct = default);

    /// <summary>Seen: one (opened), or every one when id is null (Mark all as read). Actions stay until done.</summary>
    Task MarkReadAsync(string? id, CancellationToken ct = default);
}

/// <summary>Real mode until the API has notifications: no badge, and the panel says they're on the way.</summary>
public sealed class UnavailableNotificationFeed : INotificationFeed
{
    public bool Available => false;
    public event Action? Changed { add { } remove { } }
    public Task<IReadOnlyList<AppNotification>> ReadAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AppNotification>>([]);
    public Task MarkReadAsync(string? id, CancellationToken ct = default) => Task.CompletedTask;
}

public static class NotificationWords
{
    /// <summary>The group's name, as Settings › Notifications says it.</summary>
    public static string Group(AlertGroup group) => AlertGroups.All.FirstOrDefault(info => info.Group == group)?.Title ?? "yára";

    /// <summary>The heading a line sits under in Everything: Today, Yesterday or Earlier.</summary>
    public static string Day(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var localAt = Local(at, now, zone); var localNow = Local(now, now, zone);
        return localAt.Date == localNow.Date ? "Today" : localAt.Date == localNow.Date.AddDays(-1) ? "Yesterday" : "Earlier";
    }

    /// <summary>"Just now", "12 min ago", "3 hours ago", "Yesterday", "2 Nov".</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var localAt = Local(at, now, zone); var localNow = Local(now, now, zone);
        var gap = now - at;
        if (gap < TimeSpan.FromMinutes(1)) return "Just now";
        if (gap < TimeSpan.FromHours(1)) return $"{(int)gap.TotalMinutes} min ago";
        if (gap < TimeSpan.FromHours(24) && localAt.Date == localNow.Date) return (int)gap.TotalHours == 1 ? "1 hour ago" : $"{(int)gap.TotalHours} hours ago";
        if (localAt.Date == localNow.Date.AddDays(-1)) return "Yesterday";
        return localAt.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture);
    }
    // Compare calendar dates in one zone, including the zone's offset at each instant (DST).
    // Callers without a device zone still use the supplied clock's offset, not the event's offset.
    private static DateTimeOffset Local(DateTimeOffset value, DateTimeOffset now, TimeZoneInfo? zone) =>
        zone is null ? value.ToOffset(now.Offset) : TimeZoneInfo.ConvertTime(value, zone);
}
