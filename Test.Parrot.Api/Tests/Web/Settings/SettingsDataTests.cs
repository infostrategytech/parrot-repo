using Parrot.Web.Features.Settings;

namespace Test.Parrot.Api.Tests.Web.Settings;

// The settings contracts only: defaults, one change at a time, and the rules a save checks.
public class SettingsDataTests
{
    [Fact]
    public void EveryGroupThatCanChangeHasAMenuWithItsDefaultInIt()
    {
        var defaults = NotificationSettings.Defaults;
        Assert.Equal(AlertGroups.All.Where(info => !info.Required).Select(info => info.Group), defaults.Levels.Keys);
        Assert.All(AlertGroups.All.Where(info => !info.Required), info => Assert.Contains(info.Levels, option => option.Level == info.Default));
        Assert.All(AlertGroups.All.Where(info => info.Required), info => Assert.Empty(info.Levels));
        Assert.Equal(AlertLevel.Important, defaults.LevelOf(AlertGroup.Chats));
        Assert.Equal(new[] { AlertGroup.Billing }, AlertGroups.All.Where(info => info.OwnersOnly).Select(info => info.Group));
        Assert.Equal((PopupLevel.NeedsYou, SoundLevel.Messages, EmailDelivery.Daily, WeeklySummary.Off), (defaults.Popups, defaults.Sounds, defaults.Email, defaults.Weekly));
        Assert.False(defaults.QuietHours);
        Assert.False(defaults.Push);
        Assert.False(defaults.Everything);
        Assert.Null(defaults.QuietTimeZone);
    }

    [Fact]
    public void ChangingOneGroupLeavesTheOthers()
    {
        var changed = NotificationSettings.Defaults.With(AlertGroup.Chats, AlertLevel.Everything);
        Assert.Equal(AlertLevel.Everything, changed.LevelOf(AlertGroup.Chats));
        Assert.Equal(AlertLevel.Important, NotificationSettings.Defaults.LevelOf(AlertGroup.Chats));
        Assert.Equal(NotificationSettings.Defaults.LevelOf(AlertGroup.Payments), changed.LevelOf(AlertGroup.Payments));
    }

    [Fact]
    public void OpeningHoursCoverTheWeekAndAnOpenDayMustCloseAfterItOpens()
    {
        var details = BusinessDetails.Defaults;
        Assert.Equal(7, details.Hours.Select(day => day.Day).Distinct().Count());
        Assert.False(details.Hours.Single(day => day.Day == DayOfWeek.Sunday).Open);
        Assert.Null(details.Problem);
        var wrong = details with { Hours = [.. details.Hours.Select(day => day.Day == DayOfWeek.Monday ? day with { Closes = day.Opens } : day)] };
        Assert.Contains("Monday", wrong.Problem);
        var closed = details with { Hours = [.. details.Hours.Select(day => day.Day == DayOfWeek.Monday ? day with { Open = false, Closes = day.Opens } : day)] };
        Assert.Null(closed.Problem);
    }

    [Fact]
    public async Task RealModeShowsDefaultsAndNeverClaimsToSave()
    {
        INotificationSettingsStore notifications = new UnavailableNotificationSettings();
        IBusinessDetailsStore business = new UnavailableBusinessDetails();
        Assert.False(notifications.Available);
        Assert.False(business.Available);
        Assert.Equal(NotificationSettings.Defaults, await notifications.ReadAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => notifications.SaveAsync(NotificationSettings.Defaults));
        await Assert.ThrowsAsync<NotSupportedException>(() => business.SaveAsync(BusinessDetails.Defaults));
    }
}
