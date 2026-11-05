namespace Parrot.Web.Features.Settings;

// What Settings keeps that the API doesn't have yet (designer, 1 November 2026): a person's notification choices,
// and the business details beyond its name and logo. Real mode can't save them; see settings-backend-requests.

/// <summary>Six groups rather than every event against every channel: a seller can take it in at a glance.</summary>
public enum AlertGroup { NeedsYou, Chats, Tasks, Payments, Connections, WeeklySummary }

/// <summary>Email for a group: none, one for each alert, or one summary a day.</summary>
public enum EmailAlerts { Off, Each, DailySummary }

/// <summary>CanPush: it makes sense on a phone or in a browser. CanSummarise: email can be one a day.</summary>
public sealed record AlertGroupInfo(AlertGroup Group, string Title, string Description, bool CanPush = true, bool CanSummarise = false);

public static class AlertGroups
{
    public static IReadOnlyList<AlertGroupInfo> All { get; } =
    [
        new(AlertGroup.NeedsYou, "Needs you", "An agent hands a chat to you, asks you to approve something, or a task is assigned to you"),
        new(AlertGroup.Chats, "Customers & chats", "A new conversation, or a new message in a chat you're handling"),
        new(AlertGroup.Tasks, "Tasks & bookings", "New tasks, tasks due today or late, and bookings a customer moved or cancelled", CanSummarise: true),
        new(AlertGroup.Payments, "Payments", "A payment arrived, or a payment link expired"),
        new(AlertGroup.Connections, "Connections", "A channel or tool needs reconnecting"),
        new(AlertGroup.WeeklySummary, "Weekly summary", "How the week went: chats, tasks and sales", CanPush: false),
    ];
}

public sealed record AlertChoice(AlertGroup Group, bool Push, EmailAlerts Email);

/// <summary>One person's choices. Alerts always show in yára; these add phone, browser and email. Quiet hours pause
/// those, except Needs you. Everything: the whole business's alerts, not only what's assigned to this person.</summary>
public sealed record NotificationSettings(IReadOnlyList<AlertChoice> Alerts, bool QuietHours, TimeOnly QuietFrom, TimeOnly QuietTo,
    bool Everything, int Revision)
{
    public static NotificationSettings Defaults { get; } = new(
    [
        new(AlertGroup.NeedsYou, true, EmailAlerts.Each),
        new(AlertGroup.Chats, true, EmailAlerts.Off),
        new(AlertGroup.Tasks, true, EmailAlerts.DailySummary),
        new(AlertGroup.Payments, true, EmailAlerts.Each),
        new(AlertGroup.Connections, false, EmailAlerts.Each),
        new(AlertGroup.WeeklySummary, false, EmailAlerts.Each),
    ], true, new(22, 0), new(7, 0), true, 0);

    public AlertChoice For(AlertGroup group) => Alerts.FirstOrDefault(alert => alert.Group == group) ?? new(group, false, EmailAlerts.Off);
    public NotificationSettings With(AlertChoice choice) => this with { Alerts = [.. Alerts.Where(alert => alert.Group != choice.Group).Append(choice).OrderBy(alert => alert.Group)] };
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

/// <summary>A day's opening hours, in the business's time zone.</summary>
public sealed record DayHours(DayOfWeek Day, bool Open, TimeOnly Opens, TimeOnly Closes);

public sealed record BusinessContact(string Email = "", string Phone = "", string Website = "", string Instagram = "", string TikTok = "", string Facebook = "");

/// <summary>What customers and agents know about the business, beyond the name and logo (the workspace API) and the
/// kind of business. Opening hours are data, so agents can answer "are you open?" and bookings can follow them.</summary>
public sealed record BusinessDetails(string Description, string Currency, string TimeZone, IReadOnlyList<DayHours> Hours, BusinessContact Contact, int Revision)
{
    public static BusinessDetails Defaults { get; } = new("", "NGN", "Africa/Lagos",
        [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(day => new DayHours(day, day != DayOfWeek.Sunday, new(9, 0), day == DayOfWeek.Saturday ? new(16, 0) : new(18, 0)))],
        new(), 0);

    /// <summary>A day that's open needs a closing time after its opening time.</summary>
    public string? Problem => Hours.FirstOrDefault(day => day.Open && day.Closes <= day.Opens) is { } wrong
        ? $"{wrong.Day} closes before it opens. Check its hours." : null;
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
