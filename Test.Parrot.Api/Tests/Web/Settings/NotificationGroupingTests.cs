using Parrot.Web.Features.Notifications;
using Parrot.Web.Features.Settings;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

// Generic headlines that repeat become one row with a count, as Instagram's "4 new messages" does (designer,
// 5 November 2026). Only waiting items of one type, for one person in one workspace, ever share a row.
public sealed class NotificationGroupingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static AppNotification Task(string id, NotificationType type = NotificationType.TaskNeedsAttention, string? detail = null, int minutesAgo = 0) =>
        new(id, AlertGroup.Tasks, NotificationKind.Action, NotificationMark.Task, "", "Tasks", Now.AddMinutes(-minutesAgo), true, $"/dashboard/tasks/{id}",
            Type: type, Detail: detail ?? $"Task {id}", SubjectId: id, WorkspaceId: "workspace", RecipientId: "person");

    [Fact]
    public void RepeatsOfOneTypeBecomeOneRowWithACount()
    {
        var card = Assert.Single(NotificationProjection.Build([Task("a", minutesAgo: 1), Task("b", minutesAgo: 5), Task("c", minutesAgo: 9), Task("d", minutesAgo: 20)]));
        Assert.Equal(4, card.Items.Count);
        Assert.Equal("4 tasks need your attention.", card.Title);
        var row = Assert.Single(NotificationDisplay.Rows([card], false, Now));
        Assert.Equal("View tasks", row.ActionLabel);
        Assert.Equal("Task a and 3 more", row.Detail);
    }

    [Fact]
    public void OneOfATypeKeepsItsOwnWords()
    {
        var card = Assert.Single(NotificationProjection.Build([Task("a", detail: "Check Damilola's transfer.")]));
        Assert.Equal("A task needs your attention.", card.Title);
        var row = Assert.Single(NotificationDisplay.Rows([card], false, Now));
        Assert.Equal("Open task", row.ActionLabel);
        Assert.Equal("Check Damilola's transfer.", row.Detail);
    }

    [Fact]
    public void DifferentTypesStayApart() =>
        Assert.Equal(3, NotificationProjection.Build([Task("a"), Task("b", NotificationType.TaskOverdue), Task("c", NotificationType.HumanHandoverTask)]).Count);

    [Fact]
    public void FinishedItemsNeverJoinTheWaitingGroup()
    {
        var cards = NotificationProjection.Build([Task("a"), Task("b"), Task("done") with { ResolvedAt = Now, Resolution = NotificationResolution.Completed }]);
        Assert.Equal(2, cards.Count);
        Assert.Contains(cards, card => card.Title == "2 tasks need your attention.");
    }

    [Fact]
    public void OtherPeopleAndWorkspacesNeverShareARow()
    {
        Assert.Equal(2, NotificationProjection.Build([Task("a"), Task("b") with { RecipientId = "someone-else" }]).Count);
        Assert.Equal(2, NotificationProjection.Build([Task("a"), Task("b") with { WorkspaceId = "other" }]).Count);
    }

    [Theory]
    [InlineData(NotificationType.TaskOverdue, "2 tasks are overdue.")]
    [InlineData(NotificationType.HumanHandoverTask, "2 customers have been assigned to you.")]
    [InlineData(NotificationType.TaskAssigned, "2 tasks have been assigned to you.")]
    public void EachTypeCountsInItsOwnWords(NotificationType type, string title) =>
        Assert.Equal(title, Assert.Single(NotificationProjection.Build([Task("a", type), Task("b", type)])).Title);
}
