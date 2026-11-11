using Parrot.Web.Features.Notifications;
using Parrot.Web.Features.Settings;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

public sealed class NotificationTimeTests
{
    [Theory]
    [InlineData("2026-11-04T23:30:00Z", "2026-11-05T00:05:00Z", "Africa/Lagos", "Today", "35 min ago")]
    [InlineData("2026-11-04T07:30:00Z", "2026-11-04T17:30:00Z", "America/Los_Angeles", "Yesterday", "Yesterday")]
    // The event is before the spring DST change: applying only the clock's current offset would get its day wrong.
    [InlineData("2026-03-08T07:30:00Z", "2026-03-08T12:30:00Z", "America/Los_Angeles", "Yesterday", "Yesterday")]
    [InlineData("2026-11-03T00:30:00Z", "2026-11-05T12:00:00Z", "America/Los_Angeles", "Earlier", "2 Nov")]
    public void CalendarLabelsUseTheSameUserZoneIncludingDst(string atText, string nowText, string zoneId, string day, string when)
    {
        var at = DateTimeOffset.Parse(atText); var now = DateTimeOffset.Parse(nowText); var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        Assert.Equal(day, NotificationWords.Day(at, now, zone));
        Assert.Equal(when, NotificationWords.When(at, now, zone));
        var source = new AppNotification("controlled", AlertGroup.NeedsYou, NotificationKind.Action, NotificationMark.Agent, "Question", "Inbox", at, true, "/dashboard/inbox/controlled", Type: NotificationType.AgentQuestion);
        var cards = NotificationProjection.Build([source]);
        var row = Assert.Single(NotificationDisplay.Rows(cards, true, now, zone));
        Assert.Equal(day, row.Section);
        Assert.EndsWith(when, row.Meta);
        var resolved = source with { ResolvedAt = at, Resolution = NotificationResolution.Answered };
        Assert.EndsWith(when, Assert.Single(NotificationDisplay.Rows(NotificationProjection.Build([resolved]), false, now, zone)).Meta);
    }

    [Fact]
    public void ExplicitClockOffsetsAlsoNormaliseTheEventsCalendarDay()
    {
        var at = DateTimeOffset.Parse("2026-11-04T23:30:00Z");
        var now = DateTimeOffset.Parse("2026-11-05T00:05:00Z").ToOffset(TimeSpan.FromHours(1));
        Assert.Equal("Today", NotificationWords.Day(at, now));
    }
}
