using Parrot.Web.Features.Settings;

namespace Parrot.Web.Features.Notifications;

/// <summary>
/// What reaches you while yára is open (designer, 5 November 2026). Only what's new since you opened it, and only
/// unread; pop-ups as your Pop-ups choice says; a browser notification instead when yára is in another tab and
/// you've allowed them; nothing during quiet hours, when it still waits in the bell. Words are the catalogue's,
/// so a browser notification on a lock screen says the type, never the customer.
/// </summary>
public static class NotificationDelivery
{
    public static bool PopsUp(NotificationSettings settings, AppNotification item) => settings.Popups switch
    {
        PopupLevel.Everything => true,
        PopupLevel.NeedsYou => item.NeedsYou,
        _ => false,
    };

    /// <summary>Quiet hours in their own time zone, or the person's when none was saved. Overnight spans midnight.</summary>
    public static bool Quiet(NotificationSettings settings, DateTimeOffset now, TimeZoneInfo fallback)
    {
        if (!settings.QuietHours || settings.QuietFrom == settings.QuietTo) return false;
        var zone = settings.QuietTimeZone is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var saved) ? saved : fallback;
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return settings.QuietFrom < settings.QuietTo
            ? local >= settings.QuietFrom && local < settings.QuietTo
            : local >= settings.QuietFrom || local < settings.QuietTo;
    }

    /// <summary>The unread ones not seen before, then remembers them, so each arrives once.</summary>
    public static IReadOnlyList<AppNotification> Arrived(IEnumerable<AppNotification> items, ISet<string> known)
    {
        var fresh = items.Where(item => item.Unread && item.ResolvedAt is null && !known.Contains(item.Id)).ToArray();
        foreach (var item in items) known.Add(item.Id);
        return fresh;
    }
}

/// <summary>Asks the open page's pop-up host to show a test (Settings › Notifications › Send a test).</summary>
public sealed class NotificationPopups
{
    public event Action<bool, bool>? TestRequested;

    /// <summary>A test pop-up, a test browser notification, or both, as the person's choices allow.</summary>
    public void Test(bool popup, bool browser) => TestRequested?.Invoke(popup, browser);
}
