using Parrot.Web.Features.Notifications;
using Parrot.Web.Features.Settings;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

// What pops up while yára is open: only what's new and unread, as the Pop-ups choice says, never in quiet hours.
public sealed class NotificationDeliveryTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static AppNotification Item(string id, NotificationKind kind = NotificationKind.Action, bool unread = true) =>
        new(id, AlertGroup.Tasks, kind, NotificationMark.Task, "", "Tasks", DateTimeOffset.UtcNow, unread, "/dashboard/tasks/" + id, Type: NotificationType.TaskNeedsAttention);

    [Fact]
    public void OnlyWhatArrivesAfterwardsPopsUp()
    {
        var known = new HashSet<string>();
        Assert.Single(NotificationDelivery.Arrived([Item("a")], known));
        Assert.Empty(NotificationDelivery.Arrived([Item("a")], known));
        Assert.Equal("b", Assert.Single(NotificationDelivery.Arrived([Item("a"), Item("b")], known)).Id);
        Assert.Empty(NotificationDelivery.Arrived([Item("c", unread: false)], known));
    }

    [Fact]
    public void PopupsFollowTheChoice()
    {
        var action = Item("a");
        var update = Item("b", NotificationKind.Update);
        var settings = NotificationSettings.Defaults;
        Assert.True(NotificationDelivery.PopsUp(settings with { Popups = PopupLevel.NeedsYou }, action));
        Assert.False(NotificationDelivery.PopsUp(settings with { Popups = PopupLevel.NeedsYou }, update));
        Assert.True(NotificationDelivery.PopsUp(settings with { Popups = PopupLevel.Everything }, update));
        Assert.False(NotificationDelivery.PopsUp(settings with { Popups = PopupLevel.Off }, action));
    }

    [Theory]
    [InlineData(23, true)]
    [InlineData(3, true)]
    [InlineData(7, false)]
    [InlineData(12, false)]
    public void OvernightQuietHoursSpanMidnight(int hour, bool quiet)
    {
        var settings = NotificationSettings.Defaults with { QuietHours = true, QuietFrom = new(22, 0), QuietTo = new(7, 0), QuietTimeZone = "UTC" };
        Assert.Equal(quiet, NotificationDelivery.Quiet(settings, new DateTimeOffset(2026, 11, 5, hour, 0, 0, TimeSpan.Zero), Utc));
    }

    [Fact]
    public void QuietHoursOffNeverHoldsThem() =>
        Assert.False(NotificationDelivery.Quiet(NotificationSettings.Defaults with { QuietHours = false }, new DateTimeOffset(2026, 11, 5, 23, 0, 0, TimeSpan.Zero), Utc));
}
