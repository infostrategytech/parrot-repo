using System.Globalization;

namespace Parrot.Web.Features.Settings;

/// <summary>Small UI choices over the existing public settings contract; no delivery or access enforcement.</summary>
public static class NotificationDeliveryChoices
{
    public static IReadOnlyList<TimeOnly> Times { get; } = Enumerable.Range(0, 96).Select(index => new TimeOnly(index / 4, index % 4 * 15)).ToArray();
    public static string TimeLabel(TimeOnly time) => time.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();
    public static string EmailLabel(EmailDelivery value) => value switch { EmailDelivery.AsTheyHappen => "As they happen", EmailDelivery.Daily => "Daily summary", _ => "Off" };
    public static NotificationSettings WithPopups(NotificationSettings current, bool on) => current with { Popups = on ? PopupLevel.NeedsYou : PopupLevel.Off };
    public static bool BrowserEnabled(bool wanted, string? permission) => wanted && permission == "granted";
    public static bool BrowserCanChange(string? permission) => permission is "default" or "granted";
    public static string BrowserDescription(string? permission) => permission switch
    {
        "denied" => "Blocked in this browser. To allow them, click the icon to the left of the web address, find Notifications and choose Allow. Then select Check again.",
        "unsupported" => "This browser doesn't support notifications. They still show in the bell.",
        "granted" => "Allowed in this browser.",
        "default" => "Turn on, and this browser will ask for permission.",
        _ => "Checking this browser's notification permission…"
    };

    public static string? IanaZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return null;
        return zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;
    }

    public static string ZoneLabel(string id, DateTimeOffset now)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return id;
        var city = (IanaZone(id) ?? id).Split('/')[^1].Replace('_', ' ');
        var offset = zone.GetUtcOffset(now);
        var sign = offset < TimeSpan.Zero ? "−" : "+";
        var hours = Math.Abs((int)offset.TotalHours).ToString(CultureInfo.InvariantCulture);
        var minutes = offset.Minutes == 0 ? "" : $":{Math.Abs(offset.Minutes):00}";
        return $"{city} (UTC{(offset == TimeSpan.Zero ? "" : sign + hours + minutes)})";
    }

    public static string? Problem(NotificationSettings settings) => !settings.QuietHours ? null
        : settings.QuietFrom == settings.QuietTo ? "Choose different start and end times."
        : IanaZone(settings.QuietTimeZone) is null ? "Your time zone couldn't be detected. Reload to try again." : null;
}
