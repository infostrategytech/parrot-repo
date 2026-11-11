using Parrot.Web.Features.Notifications;
using Parrot.Web.Features.Settings;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

public sealed class NotificationFeedTests
{
    private static AppNotification Item(NotificationKind kind, DateTimeOffset? resolved = null) =>
        new("n", AlertGroup.NeedsYou, kind, NotificationMark.Chat, "Text", "Inbox", DateTimeOffset.Now, true, null, resolved);

    [Fact]
    public void OnlyActionsStillToDoNeedYou()
    {
        Assert.True(Item(NotificationKind.Action).NeedsYou);
        Assert.False(Item(NotificationKind.Action, DateTimeOffset.Now).NeedsYou);
        Assert.False(Item(NotificationKind.Update).NeedsYou);
    }

    [Fact]
    public void TheBellNamesGroupsInSettingsWords() =>
        Assert.All(AlertGroups.All, info => Assert.Equal(info.Title, NotificationWords.Group(info.Group)));

    [Fact]
    public void NeedsYouAndSecurityAreAlwaysOnWhateverWasSaved()
    {
        var saved = NotificationSettings.Defaults with { Levels = new Dictionary<AlertGroup, AlertLevel> { [AlertGroup.Account] = AlertLevel.Off, [AlertGroup.NeedsYou] = AlertLevel.Off } };
        Assert.Equal(AlertLevel.Everything, saved.LevelOf(AlertGroup.Account));
        Assert.Equal(AlertLevel.Everything, saved.LevelOf(AlertGroup.NeedsYou));
        Assert.Same(saved, saved.With(AlertGroup.Account, AlertLevel.Off));
        Assert.Equal(new[] { AlertGroup.NeedsYou, AlertGroup.Account }, AlertGroups.All.Where(info => info.Required).Select(info => info.Group));
    }

    [Fact]
    public void ReadingAnActionKeepsItInNeedsYou()
    {
        var read = Item(NotificationKind.Action) with { Unread = false };
        Assert.True(read.NeedsYou);
        Assert.False(read.Unread);
        Assert.False((read with { ResolvedAt = DateTimeOffset.UtcNow }).NeedsYou);
    }

    [Fact]
    public void Times()
    {
        var now = new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal("Just now", NotificationWords.When(now.AddSeconds(-20), now));
        Assert.Equal("12 min ago", NotificationWords.When(now.AddMinutes(-12), now));
        Assert.Equal("3 hours ago", NotificationWords.When(now.AddHours(-3), now));
        Assert.Equal("Yesterday", NotificationWords.When(now.AddDays(-1), now));
        Assert.Equal("1 Nov", NotificationWords.When(now.AddDays(-2), now));
        Assert.Equal(new[] { "Today", "Yesterday", "Earlier" }, new[] { now, now.AddDays(-1), now.AddDays(-2) }.Select(at => NotificationWords.Day(at, now)));
    }
}
