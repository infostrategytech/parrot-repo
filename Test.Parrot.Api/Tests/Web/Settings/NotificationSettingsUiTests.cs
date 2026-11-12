using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.Services;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Settings;
using Parrot.Web.Services;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

public sealed class NotificationSettingsUiTests
{
    [Fact]
    public async Task SimpleSettingsHaveOnlyTwoSectionsWithoutTheOldPolicyBuilder()
    {
        var store = new ControlledSettings(NotificationSettings.Defaults);
        var html = await RenderAsync(store);
        Assert.Equal(2, html.Split("settings-card__body", StringSplitOptions.None).Length - 1);
        Assert.Contains("In-app pop-ups", html);
        Assert.Contains("Message sounds", html);
        Assert.Contains("Browser notifications", html);
        Assert.Contains("Email notifications", html);
        Assert.Contains("Quiet hours", html);
        foreach (var removed in new[] { "What to tell you about", "Whose notifications", "Weekly summary", "Always", "Phone &amp; browser" }) Assert.DoesNotContain(removed, html);
        Assert.DoesNotContain("notification-quiet-schedule", html);
        // No "coming soon" banner: pop-ups and browser notifications work while yára is open, and there's a test.
        Assert.DoesNotContain("coming soon", html);
        Assert.Contains("Try it", html);
        Assert.DoesNotContain("Simulate a new one", html);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task ScheduleOnlyAppearsWhenEnabledAndNamesItsSavedZone()
    {
        var html = await RenderAsync(new ControlledSettings(NotificationSettings.Defaults with { QuietHours = true, QuietTimeZone = "Africa/Lagos" }));
        Assert.Contains("notification-quiet-schedule", html);
        Assert.Contains(">From<", html);
        Assert.Contains(">Until<", html);
        Assert.Contains("Lagos", html);
        Assert.DoesNotContain("security still come through", html);
        Assert.DoesNotContain("Needs you&quot;", html);
    }

    [Fact]
    public async Task RealUnavailableModeDoesNotPretendToSaveOrDeliver()
    {
        var html = await RenderAsync(new UnavailableNotificationSettings());
        Assert.Contains("Your choices can&#x27;t be saved yet", html);
        Assert.Contains("Message sounds still work on this device", html);
        Assert.Contains("aria-disabled=\"true\"", html);
        Assert.DoesNotContain("Preferences saved.", html);
        Assert.DoesNotContain("even when yára is closed", html);
    }

    [Fact]
    public async Task FailedLoadRequiresReloadInsteadOfSavingOverUnknownPreferences()
    {
        var store = new ControlledSettings(NotificationSettings.Defaults, failRead: true);
        var html = await RenderAsync(store);
        Assert.Contains("Reload settings", html);
        Assert.Contains("Reload before making changes", html);
        Assert.Contains("aria-disabled=\"true\"", html);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void HiddenLegacyPreferencesAreNotResetWhenAToggleChanges()
    {
        var legacy = NotificationSettings.Defaults with { Weekly = WeeklySummary.Friday, Everything = true, Sounds = SoundLevel.All, Revision = 7 };
        var on = NotificationDeliveryChoices.WithPopups(legacy, true);
        Assert.Equal(PopupLevel.NeedsYou, on.Popups);
        Assert.Equal(legacy, on with { Popups = legacy.Popups });
        Assert.Equal(PopupLevel.Off, NotificationDeliveryChoices.WithPopups(legacy, false).Popups);
        Assert.Equal(legacy.Levels, on.Levels);
    }

    [Fact]
    public void QuietSchedulesAllowOvernightButRejectEqualTimesAndUnknownZones()
    {
        var overnight = NotificationSettings.Defaults with { QuietHours = true, QuietTimeZone = "Africa/Lagos" };
        Assert.Null(NotificationDeliveryChoices.Problem(overnight));
        Assert.NotNull(NotificationDeliveryChoices.Problem(overnight with { QuietTo = overnight.QuietFrom }));
        Assert.NotNull(NotificationDeliveryChoices.Problem(overnight with { QuietTimeZone = "Not/AZone" }));
        Assert.NotNull(NotificationDeliveryChoices.Problem(overnight with { QuietTimeZone = null }));
        Assert.Null(NotificationDeliveryChoices.Problem(overnight with { QuietHours = false, QuietTimeZone = null }));
    }

    [Fact]
    public void TimeChoicesCoverTheWholeDayAndZoneLabelsFollowDaylightSaving()
    {
        Assert.Equal(96, NotificationDeliveryChoices.Times.Distinct().Count());
        Assert.Contains(new TimeOnly(12, 30), NotificationDeliveryChoices.Times);
        Assert.Contains(new TimeOnly(0, 0), NotificationDeliveryChoices.Times);
        Assert.Equal("10:00 pm", NotificationDeliveryChoices.TimeLabel(new(22, 0)));
        Assert.Contains("UTC−8", NotificationDeliveryChoices.ZoneLabel("America/Los_Angeles", new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)));
        Assert.Contains("UTC−7", NotificationDeliveryChoices.ZoneLabel("America/Los_Angeles", new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("default", false, true)]
    [InlineData("denied", false, false)]
    [InlineData("unsupported", false, false)]
    [InlineData("granted", true, true)]
    public void BrowserPermissionAndPreferenceRemainSeparate(string? permission, bool effective, bool canChange)
    {
        Assert.Equal(effective, NotificationDeliveryChoices.BrowserEnabled(true, permission));
        Assert.False(NotificationDeliveryChoices.BrowserEnabled(false, permission));
        Assert.Equal(canChange, NotificationDeliveryChoices.BrowserCanChange(permission));
        Assert.DoesNotContain("even when", NotificationDeliveryChoices.BrowserDescription(permission));
        // "Coming soon" is said once, in the page's banner; the row only says what the browser allows.
        if (permission == "granted") Assert.Equal("Allowed in this browser.", NotificationDeliveryChoices.BrowserDescription(permission));
    }

    private static async Task<string> RenderAsync(INotificationSettingsStore store)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton(store).AddSingleton<IJSRuntime>(new NoJs())
            .AddSingleton<AuroraInterop>().AddSingleton<FeedbackService>().AddSingleton<AuthState>()
            .AddSingleton<global::Parrot.Web.Features.Notifications.NotificationPopups>()
            .AddSingleton<global::Parrot.Web.Features.Dashboard.TaskRequests.ICustomerHandoverTasks, global::Parrot.Web.Features.Dashboard.TaskRequests.UnavailableCustomerHandoverTasks>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<global::Parrot.Web.Components.Pages.Dashboard.Settings.NotificationsSection>();
            return component.ToHtmlString();
        });
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("No browser in static rendering.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
    private sealed class ControlledSettings(NotificationSettings settings, bool failRead = false) : INotificationSettingsStore
    {
        public bool Available => true;
        public int Saves { get; private set; }
        public Task<NotificationSettings> ReadAsync(CancellationToken ct = default) => failRead ? throw new HttpRequestException("Controlled read failure") : Task.FromResult(settings);
        public Task<NotificationSettings> SaveAsync(NotificationSettings value, CancellationToken ct = default) { Saves++; return Task.FromResult(value with { Revision = value.Revision + 1 }); }
    }
}
