namespace Parrot.Web.Features.Settings;

// What Settings keeps that the API doesn't have yet (designer, 1 November 2026): a person's notification choices,
// and the business details beyond its name and logo. Real mode can't save them; see settings-backend-requests.

/// <summary>A few groups rather than every event: a seller can take it in at a glance. The bell and Settings use the
/// same ones (designer, 3 November 2026), so turning one down matches what was seen.</summary>
public enum AlertGroup { NeedsYou, Chats, Tasks, Payments, Connections, Billing, Account }

/// <summary>How much of a group reaches you outside yára: all of it, the part that matters, or none.</summary>
public enum AlertLevel { Everything, Important, Off }

/// <summary>One choice in a group's menu, in its own words, with a line saying what it means.</summary>
public sealed record AlertLevelOption(AlertLevel Level, string Label, string Detail);

/// <summary>Levels: the group's menu, first to last; empty when it's always on (Required). OwnersOnly: only the owner
/// sees it (the plan and its billing).</summary>
public sealed record AlertGroupInfo(AlertGroup Group, string Title, string Description, IReadOnlyList<AlertLevelOption> Levels,
    AlertLevel Default = AlertLevel.Everything, bool Required = false, bool OwnersOnly = false);

public static class AlertGroups
{
    public static IReadOnlyList<AlertGroupInfo> All { get; } =
    [
        new(AlertGroup.NeedsYou, "Needs you", "Hand-overs, approvals and tasks assigned to you", [], Required: true),
        new(AlertGroup.Chats, "Customers & chats", "New conversations and messages",
            [new(AlertLevel.Everything, "Every message", "Each new message in a chat you're handling"),
             new(AlertLevel.Important, "New chats only", "When a customer starts a conversation"),
             new(AlertLevel.Off, "Off", "New messages still show in Inbox")], AlertLevel.Important),
        new(AlertGroup.Tasks, "Tasks & bookings", "Tasks your agent creates, and bookings",
            [new(AlertLevel.Everything, "New, due, late and booked", "New tasks, tasks due today or late, and bookings made, moved or cancelled"),
             new(AlertLevel.Important, "Only due and late", "Tasks due today or running late"),
             new(AlertLevel.Off, "Off", "They still show in the bell and in Tasks")]),
        new(AlertGroup.Payments, "Payments", "What your customers pay",
            [new(AlertLevel.Everything, "Every payment", "Each payment that arrives, and any that fail"),
             new(AlertLevel.Important, "Only problems", "Failed payments and expired payment links"),
             new(AlertLevel.Off, "Off", "They still show in the bell")]),
        new(AlertGroup.Connections, "Connections", "Channels, tools and agents",
            [new(AlertLevel.Important, "When something breaks", "A channel, tool or agent stops working"),
             new(AlertLevel.Off, "Off", "It still shows in the bell")], AlertLevel.Important),
        new(AlertGroup.Billing, "Plan & billing", "Your yára plan",
            [new(AlertLevel.Important, "Limits and problems", "Close to your conversation limit, or a plan payment failed"),
             new(AlertLevel.Everything, "Also receipts", "Renewals and receipts too"),
             new(AlertLevel.Off, "Off", "They still show in the bell")], AlertLevel.Important, OwnersOnly: true),
        new(AlertGroup.Account, "Account & security", "New sign-ins, password changes and your team", [], Required: true),
    ];

    public static AlertGroupInfo Of(AlertGroup group) => All.First(info => info.Group == group);
}

/// <summary>Pop-ups while yára is open: everything, only what needs you, or none.</summary>
public enum PopupLevel { Everything, NeedsYou, Off }

/// <summary>Sounds while yára is open: for messages and notifications, for new messages, or none.</summary>
public enum SoundLevel { All, Messages, Off }

/// <summary>Email: each one as it happens, one a day, or none.</summary>
public enum EmailDelivery { AsTheyHappen, Daily, Off }

/// <summary>The weekly summary by email: Monday morning, Friday evening, or none.</summary>
public enum WeeklySummary { Monday, Friday, Off }

/// <summary>
/// One person's delivery preferences. The simplified UI (4 November 2026) exposes pop-ups, browser delivery,
/// email and an optional quiet schedule. Sounds are controlled on the device. Weekly, Levels, Everything and
/// Sounds remain for compatibility and round-trip unchanged; they are not new permissions or delivery promises.
/// QuietTimeZone is an IANA zone, so the server need not guess an overnight schedule's time zone or DST rules.
/// </summary>
public sealed record NotificationSettings(PopupLevel Popups, SoundLevel Sounds, bool Push, EmailDelivery Email, WeeklySummary Weekly,
    IReadOnlyDictionary<AlertGroup, AlertLevel> Levels, bool QuietHours, TimeOnly QuietFrom, TimeOnly QuietTo, bool Everything, int Revision,
    string? QuietTimeZone = null)
{
    public static NotificationSettings Defaults { get; } = new(PopupLevel.NeedsYou, SoundLevel.Messages, false, EmailDelivery.Daily, WeeklySummary.Off,
        AlertGroups.All.Where(info => !info.Required).ToDictionary(info => info.Group, info => info.Default), false, new(22, 0), new(7, 0), false, 0);

    /// <summary>A group's level; always Everything for one that can't be turned down (Needs you, security).</summary>
    public AlertLevel LevelOf(AlertGroup group) => AlertGroups.Of(group) is { Required: true } ? AlertLevel.Everything
        : Levels.TryGetValue(group, out var level) ? level : AlertGroups.Of(group).Default;

    public NotificationSettings With(AlertGroup group, AlertLevel level) => AlertGroups.Of(group).Required ? this
        : this with { Levels = new Dictionary<AlertGroup, AlertLevel>(Levels) { [group] = level } };
}

public interface INotificationSettingsStore
{
    bool Available { get; }
    Task<NotificationSettings> ReadAsync(CancellationToken ct = default);
    Task<NotificationSettings> SaveAsync(NotificationSettings settings, CancellationToken ct = default);
}

/// <summary>No preferences API yet: the defaults are shown and nothing is saved or claimed to be.</summary>
public sealed class UnavailableNotificationSettings : INotificationSettingsStore
{
    public bool Available => false;
    public Task<NotificationSettings> ReadAsync(CancellationToken ct = default) => Task.FromResult(NotificationSettings.Defaults);
    public Task<NotificationSettings> SaveAsync(NotificationSettings settings, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>One stretch of opening time in a day.</summary>
public sealed record TimeRange(TimeOnly Opens, TimeOnly Closes);

/// <summary>
/// A day's opening hours, in the business's time zone. Opens–Closes is the day's first stretch; as Google Business
/// Profile allows (designer, 7 November 2026), a day can also be open 24 hours, or have more stretches after a
/// break (9am–1pm, then 2pm–6pm) in <see cref="More"/>.
/// </summary>
public sealed record DayHours(DayOfWeek Day, bool Open, TimeOnly Opens, TimeOnly Closes, bool AllDay = false, IReadOnlyList<TimeRange>? More = null)
{
    /// <summary>Every stretch of the day, in order; one that runs all day when <see cref="AllDay"/>.</summary>
    public IReadOnlyList<TimeRange> Ranges => AllDay ? [new(TimeOnly.MinValue, TimeOnly.MaxValue)] : [new(Opens, Closes), .. More ?? []];

    /// <summary>What's wrong with the times, if anything: a stretch that closes before it opens, or overlaps the one before.</summary>
    public string? TimesProblem(string name)
    {
        if (!Open || AllDay) return null;
        var ranges = Ranges;
        for (var i = 0; i < ranges.Count; i++)
        {
            if (ranges[i].Closes <= ranges[i].Opens) return $"{name} closes before it opens. Check its hours.";
            if (i > 0 && ranges[i].Opens < ranges[i - 1].Closes) return $"{name}'s hours overlap. Start each after the one before ends.";
        }
        return null;
    }
}

/// <summary>A date with other hours than its weekday's: a holiday it's closed, or a day it opens late. Open 24 hours
/// or one stretch, kept simple (Google Business Profile's special hours, designer, 7 November 2026).</summary>
public sealed record SpecialDay(DateOnly Date, bool Open, TimeOnly Opens, TimeOnly Closes, bool AllDay = false);

/// <summary>How customers reach the business. WhatsApp is its own number (designer, 7 November 2026): vendors sell
/// there, and it isn't always the line they take calls on. Facebook is the page's name, as in facebook.com/name.</summary>
public sealed record BusinessContact(string Email = "", string Phone = "", string Website = "", string Instagram = "", string TikTok = "", string Facebook = "", string WhatsApp = "")
{
    /// <summary>An email that's there but can't be one. Empty is fine: every contact is optional.</summary>
    public string? EmailProblem => Email.Trim() is { Length: > 0 } email
        && (email.IndexOf('@') is var at && (at < 1 || email.IndexOf('.', at) < at + 2 || email.EndsWith('.') || email.Contains(' ')))
        ? "Enter an email like hello@yourshop.com." : null;

    /// <summary>A Facebook page however it was pasted (a link, or with www.), kept as the page's name.</summary>
    public static string FacebookPage(string? value)
    {
        var page = (value ?? string.Empty).Trim();
        foreach (var start in new[] { "https://", "http://", "www.", "m.", "facebook.com/", "fb.com/" })
            if (page.StartsWith(start, StringComparison.OrdinalIgnoreCase)) page = page[start.Length..];
        return page.TrimEnd('/');
    }
}

/// <summary>What customers and agents know about the business, beyond the name and logo (the workspace API) and the
/// kind of business. Opening hours are data, so agents can answer "are you open?" and bookings can follow them.</summary>
/// <param name="TemporarilyClosed">Closed for now (travelling, restocking): agents say so, and the hours are kept for reopening.</param>
/// <param name="SpecialDays">Dates with other hours than usual, such as holidays.</param>
public sealed record BusinessDetails(string Description, string Currency, string TimeZone, IReadOnlyList<DayHours> Hours, BusinessContact Contact, int Revision,
    bool TemporarilyClosed = false, IReadOnlyList<SpecialDay>? SpecialDays = null)
{
    public static BusinessDetails Defaults { get; } = new("", "NGN", "Africa/Lagos",
        [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(day => new DayHours(day, day != DayOfWeek.Sunday, new(9, 0), day == DayOfWeek.Saturday ? new(16, 0) : new(18, 0)))],
        new(), 0);

    /// <summary>An open day or special day needs each stretch to close after it opens, and not overlap the one before.
    /// Two special days can't share a date.</summary>
    public string? Problem
    {
        get
        {
            if (Hours.Select(day => day.TimesProblem(day.Day.ToString())).FirstOrDefault(problem => problem is not null) is { } hours) return hours;
            var special = SpecialDays ?? [];
            if (special.FirstOrDefault(day => day.Open && !day.AllDay && day.Closes <= day.Opens) is { } wrong)
                return $"{wrong.Date.ToString("d MMMM", System.Globalization.CultureInfo.InvariantCulture)} closes before it opens. Check its hours.";
            if (special.GroupBy(day => day.Date).FirstOrDefault(group => group.Count() > 1) is { } twice)
                return $"{twice.Key.ToString("d MMMM", System.Globalization.CultureInfo.InvariantCulture)} is listed twice. Remove one.";
            return null;
        }
    }
}

public interface IBusinessDetailsStore
{
    bool Available { get; }
    Task<BusinessDetails> ReadAsync(CancellationToken ct = default);
    Task<BusinessDetails> SaveAsync(BusinessDetails details, CancellationToken ct = default);
}

public sealed class UnavailableBusinessDetails : IBusinessDetailsStore
{
    public bool Available => false;
    public Task<BusinessDetails> ReadAsync(CancellationToken ct = default) => Task.FromResult(BusinessDetails.Defaults);
    public Task<BusinessDetails> SaveAsync(BusinessDetails details, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>The few choices Settings offers for currency and the business's time zone.</summary>
public static class BusinessChoices
{
    public static IReadOnlyList<(string Code, string Name)> Currencies { get; } = [("NGN", "Naira (₦)"), ("GHS", "Cedi (GH₵)"), ("USD", "US dollar ($)"), ("GBP", "Pound (£)")];
    public static IReadOnlyList<(string Id, string Name)> TimeZones { get; } =
        [("Africa/Lagos", "Lagos (WAT)"), ("Africa/Accra", "Accra (GMT)"), ("Europe/London", "London (UK time)"), ("UTC", "UTC")];
    public static string CurrencyName(string code) => Currencies.FirstOrDefault(item => item.Code == code).Name ?? code;
    public static string TimeZoneName(string id) => TimeZones.FirstOrDefault(item => item.Id == id).Name ?? id;
}
