using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Layout;
using Parrot.SharedComponents.Services;
using Parrot.Web.Features.Notifications;
using Parrot.Web.Features.Settings;
using Parrot.Web.Services;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

public sealed class NotificationViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static AppNotification Question(string id, bool unread = true) => new(id, AlertGroup.NeedsYou, NotificationKind.Action,
        NotificationMark.Agent, "Question", "Inbox", Now, unread, "/dashboard/inbox/controlled",
        Type: NotificationType.AgentQuestion, AgentId: "agent", AgentName: "Ada", WorkspaceId: "workspace", RecipientId: "person");

    [Theory]
    [InlineData(null, NotificationView.NeedsYou)]
    [InlineData("unknown", NotificationView.NeedsYou)]
    [InlineData("needs-you", NotificationView.NeedsYou)]
    [InlineData("everything", NotificationView.Everything)]
    [InlineData("EVERYTHING", NotificationView.Everything)]
    public void QueryValuesHaveSafeDefaults(string? value, NotificationView expected) => Assert.Equal(expected, NotificationViews.Parse(value));

    [Theory]
    [InlineData(NotificationView.NeedsYou, false, "/dashboard/notifications?view=needs-you")]
    [InlineData(NotificationView.Everything, false, "/dashboard/notifications?view=everything")]
    [InlineData(NotificationView.Everything, true, "/dashboard/notifications?view=everything&unread=true")]
    public void FullPageLinksPreserveTheChosenView(NotificationView view, bool unread, string href) => Assert.Equal(href, NotificationViews.Href(view, unread));

    [Fact]
    public void TheRailBadgeTurnsRedOnlyWhileSomethingThatNeedsYouIsUnread()
    {
        var payment = Question("payment") with { AgentId = "payments", Kind = NotificationKind.Update, Type = NotificationType.PaymentReceived };
        var readQuestion = Question("read", false) with { AgentId = "other" };
        var resolved = Question("resolved") with { AgentId = "done", ResolvedAt = Now, Resolution = NotificationResolution.Answered };
        // Routine updates, read questions and resolved ones keep the brand colour.
        var quiet = NotificationProjection.Build([payment, readQuestion, resolved]);
        Assert.Equal(0, NotificationViews.UnreadNeedingYou(quiet));
        Assert.Equal("unread notifications", NotificationViews.BadgeMeaning(2, 0));
        // An unread question turns it red, and the name says how many need you.
        var waiting = NotificationProjection.Build([payment, readQuestion, Question("unread")]);
        Assert.Equal(1, NotificationViews.UnreadNeedingYou(waiting));
        Assert.Equal("unread notifications, 1 needs you", NotificationViews.BadgeMeaning(2, 1));
        Assert.Equal("unread notifications, 3 need you", NotificationViews.BadgeMeaning(5, 3));
        Assert.Equal("unread notifications that need you", NotificationViews.BadgeMeaning(5, 5));
        Assert.Equal("unread notification that needs you", NotificationViews.BadgeMeaning(1, 1));
    }

    [Fact]
    public void UnreadIsAnOptionalFilterNotAResolution()
    {
        var readQuestion = Question("read") with { AgentId = "other", Unread = false };
        var update = Question("update") with { Kind = NotificationKind.Update, Type = NotificationType.PaymentReceived };
        var resolved = Question("resolved") with { ResolvedAt = Now, Resolution = NotificationResolution.Answered };
        var cards = NotificationProjection.Build([Question("unread"), readQuestion, update, resolved]);
        Assert.Equal(4, NotificationViews.Filter(cards, NotificationView.Everything).Count);
        Assert.Equal(2, NotificationViews.Filter(cards, NotificationView.NeedsYou).Count);
        Assert.Single(NotificationViews.Filter(cards, NotificationView.NeedsYou, true));
        Assert.Equal(3, NotificationViews.Filter(cards, NotificationView.Everything, true).Count);
        Assert.True(readQuestion.NeedsYou);
        Assert.False(NotificationViews.ParseUnread("not-a-bool"));
        Assert.True(NotificationViews.ParseUnread("true"));
    }

    [Fact]
    public void GroupRowsOpenTheListTheirItemsAreIn()
    {
        var card = Assert.Single(NotificationProjection.Build([Question("one"), Question("two", false)]));
        var row = Assert.Single(NotificationDisplay.Rows([card], true, Now));
        Assert.Equal("Today", row.Section);
        Assert.Contains("2 questions", row.Title);
        Assert.Equal("View questions", row.ActionLabel);
        Assert.True(card.Unread);
        // No picker: a group of questions opens the Inbox, a group of tasks given to you opens Tasks on Mine.
        Assert.Equal("/dashboard/inbox", card.Href);
        var assigned = Question("task-a") with { Type = NotificationType.TaskAssigned, Mark = NotificationMark.Task, Place = "Tasks", Href = "/dashboard/tasks/a", AgentId = null };
        Assert.Equal("/dashboard/tasks?view=mine", Assert.Single(NotificationProjection.Build([assigned, assigned with { Id = "task-b", Href = "/dashboard/tasks/b" }])).Href);
        var attention = assigned with { Type = NotificationType.TaskNeedsAttention };
        Assert.Equal("/dashboard/tasks", Assert.Single(NotificationProjection.Build([attention, attention with { Id = "task-b", Href = "/dashboard/tasks/b?x=1" }])).Href);
        Assert.Equal("/dashboard/tasks/a", Assert.Single(NotificationProjection.Build([assigned])).Href);
    }

    [Fact]
    public void ResolvedActionsLoseTheirCallToActionNotTheirHistory()
    {
        var card = Assert.Single(NotificationProjection.Build([Question("resolved") with { ResolvedAt = Now, Resolution = NotificationResolution.Declined }]));
        var row = Assert.Single(NotificationDisplay.Rows([card], false, Now));
        Assert.True(row.Resolved);
        Assert.Null(row.ActionLabel);
        Assert.Contains("Declined", row.Meta);
    }

    [Theory]
    [InlineData(NotificationResolution.Completed, true, IconName.RiCheckboxCircleFill)]
    [InlineData(NotificationResolution.Answered, true, IconName.RiCheckboxCircleFill)]
    [InlineData(NotificationResolution.Approved, true, IconName.RiCheckboxCircleFill)]
    [InlineData(NotificationResolution.Repaired, true, IconName.RiCheckboxCircleFill)]
    [InlineData(NotificationResolution.Declined, false, IconName.RiCloseFill)]
    [InlineData(NotificationResolution.Cancelled, false, IconName.RiCloseFill)]
    [InlineData(NotificationResolution.Withdrawn, false, IconName.RiInformationFill)]
    [InlineData(NotificationResolution.Unavailable, false, IconName.RiInformationFill)]
    [InlineData(NotificationResolution.WaitingOnCustomer, false, IconName.RiTimeFill)]
    public void ResolvedVisualsRepresentTheActualOutcome(NotificationResolution outcome, bool success, IconName glyph)
    {
        var card = Assert.Single(NotificationProjection.Build([Question("resolved") with { ResolvedAt = Now, Resolution = outcome }]));
        var row = Assert.Single(NotificationDisplay.Rows([card], true, Now));
        Assert.True(row.Resolved);
        Assert.Null(row.ActionLabel);
        Assert.Equal(glyph, row.Glyph);
        Assert.Equal(success ? NotificationListTone.Success : NotificationListTone.Neutral, row.Tone);
    }

    [Theory]
    [InlineData(1, "1 notification needs your attention.")]
    [InlineData(8, "8 notifications need your attention.")]
    public void AttentionCountNamesItsUnitNotItsChildQuestionCount(int count, string expected) => Assert.Equal(expected, NotificationViews.NeedsYouDescription(count));

    [Theory]
    [InlineData(NotificationType.TaskNeedsAttention, "A task needs your attention.")]
    [InlineData(NotificationType.TaskAssigned, "A task has been assigned to you.")]
    [InlineData(NotificationType.HumanHandoverTask, "A customer has been assigned to you.")]
    [InlineData(NotificationType.TaskDue, "A task is due soon.")]
    [InlineData(NotificationType.TaskOverdue, "A task is overdue.")]
    public void TaskHeadlinesStayBusinessNeutralAndContextStaysSecondary(NotificationType type, string expected)
    {
        foreach (var context in new[] { "Send Kemi a quote", "Check the vehicle specification", "Confirm the catering requirements" })
        {
            var task = Question("task") with { Type = type, Mark = NotificationMark.Task, Place = "Tasks", Detail = context, AgentId = null };
            // The same adapter supplies both the page's dated view and the compact Needs you panel.
            foreach (var sections in new[] { false, true })
            {
                var row = Assert.Single(NotificationDisplay.Rows(NotificationProjection.Build([task]), sections, Now));
                Assert.Equal(expected, row.Title);
                Assert.Equal(context, row.Detail);
                Assert.Equal("Tasks · Just now", row.Meta);
                Assert.Equal(NotificationListTone.Neutral, row.Tone);
                Assert.Equal("Open task", row.ActionLabel);
            }
            var fallback = Assert.Single(NotificationDisplay.Rows(NotificationProjection.Build([task with { Detail = null }]), false, Now));
            Assert.Equal(expected, fallback.Title);
        }
    }

    [Fact]
    public void ConfirmedOutcomesKeepTheFixedHeadlineAndTheirSpecificsUnderIt()
    {
        var payment = Question("payment") with { Type = NotificationType.PaymentReceived, Kind = NotificationKind.Update, Text = "Kemi paid ₦20,000.", Detail = null };
        var row = Assert.Single(NotificationDisplay.Rows(NotificationProjection.Build([payment]), false, Now));
        Assert.Equal("A payment was received.", row.Title);
        Assert.Equal("Kemi paid ₦20,000.", row.Detail);
    }

    [Fact]
    public void KnowledgeAndHandoverUseTheirOwnMeaningfulIcons()
    {
        var rows = NotificationDisplay.Rows(NotificationProjection.Build([
            Question("knowledge") with { Type = NotificationType.KnowledgeReview, Mark = NotificationMark.Waiting },
            Question("handover") with { Type = NotificationType.HumanHandoverTask, Mark = NotificationMark.Task }
        ]), false, Now);
        Assert.Equal(IconName.RiBookOpenFill, rows.Single(row => row.Id.EndsWith("knowledge")).Glyph);
        Assert.Equal(IconName.RiUserSharedFill, rows.Single(row => row.Id.EndsWith("handover")).Glyph);
    }

    [Theory]
    [InlineData("/dashboard/notifications", "Notifications")]
    [InlineData("/dashboard/notifications/", "Notifications")]
    [InlineData("/dashboard", "Overview")]
    [InlineData("/dashboard/tasks/task-id", "Tasks")]
    [InlineData("/dashboard/inbox/thread-id", "Inbox")]
    [InlineData("/dashboard/settings/profile", "Settings")]
    public void NotificationRouteDoesNotSelectOverviewOrDuplicateARailRow(string path, string expected)
    {
        Assert.Equal(expected, NavMap.Match(path)?.Label);
        Assert.DoesNotContain(NavMap.Primary, entry => entry == NavMap.Notifications);
        // Settings opens from the account menu, not a rail row.
        Assert.DoesNotContain(NavMap.Primary, entry => entry.Label == "Settings");
    }

    [Fact]
    public async Task CurrentNotificationPageDoesNotClaimItsPopoverIsOpen()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<NavRailItem>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(NavRailItem.Entry)] = NavMap.Notifications, [nameof(NavRailItem.IsActive)] = true,
                [nameof(NavRailItem.CurrentPage)] = true, [nameof(NavRailItem.Expanded)] = false,
                [nameof(NavRailItem.OnClick)] = EventCallback.Factory.Create(this, () => { })
            }));
            return result.ToHtmlString();
        });
        Assert.Contains("aria-current=\"page\"", html);
        Assert.Contains("aria-expanded=\"false\"", html);
        Assert.Contains("is-active", html);
    }

    [Fact]
    public async Task SharedListRendersAccessibleUnreadAndSourceControls()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var rows = NotificationDisplay.Rows(NotificationProjection.Build([Question("one")]), true, Now);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<NotificationList>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(NotificationList.Items)] = rows }));
            return result.ToHtmlString();
        });
        Assert.Contains("aria-label=\"Notifications\"", html);
        Assert.Contains("aria-label=\"Unread\"", html);
        Assert.Contains("Ada has a question for you.", html);
        Assert.Contains("Answer Ada", html);
        Assert.Contains("Today", html);
        Assert.Contains("type=\"button\"", html);
    }

    [Fact]
    public async Task FullPageListUsesOneRowControlWithATrailingAction()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var rows = NotificationDisplay.Rows(NotificationProjection.Build([Question("one")]), false, Now);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<NotificationList>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(NotificationList.Items)] = rows, [nameof(NotificationList.Layout)] = NotificationListLayout.Feed
            }));
            return result.ToHtmlString();
        });
        Assert.Contains("ds-notification-list--feed", html);
        Assert.Contains("ds-notification-list__trailing", html);
        Assert.Contains("Answer Ada", html);
        Assert.Equal(1, html.Split("<button", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task FullPageUsesTheFeedWithoutMarkingAnythingReadOnArrival()
    {
        var feed = new ControlledFeed([Question("one"), Question("two", false)]);
        var html = await RenderPageAsync(feed);
        Assert.Contains("Notifications", html);
        Assert.Contains("2 questions", html);
        Assert.Contains("All notifications", html);
        Assert.Contains("Mark all as read", html);
        Assert.Contains("1 notification needs your attention.", html);
        Assert.DoesNotContain("Related items appear together.", html);
        Assert.Contains("ds-tabs--tray", html);
        Assert.Contains("ds-notification-list--feed", html);
        Assert.Equal(0, feed.ReadMutations);
    }

    [Fact]
    public async Task FullPageDoesNotPretendTheProductionFeedExists()
    {
        var html = await RenderPageAsync(new UnavailableNotificationFeed());
        Assert.Contains("Notifications are on the way", html);
        Assert.DoesNotContain("Mark all as read", html);
        Assert.DoesNotContain("All notifications", html);
    }

    [Fact]
    public async Task FailedFeedNeverClaimsThePersonIsCaughtUp()
    {
        var html = await RenderPageAsync(new ControlledFeed([], failRead: true));
        Assert.Contains("Your notifications couldn&#x27;t be refreshed", html);
        Assert.Contains("Try again", html);
        Assert.DoesNotContain("all caught up", html);
        Assert.DoesNotContain("Nothing needs you", html);
    }

    [Fact]
    public async Task SuccessfulEmptyFeedCanStillConfirmThereIsNothingPending()
    {
        var html = await RenderPageAsync(new ControlledFeed([]));
        Assert.Contains("all caught up", html);
        Assert.Contains("Nothing needs you", html);
    }

    private static async Task<string> RenderPageAsync(INotificationFeed feed)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton(feed)
            .AddSingleton<NavigationManager>(new ControlledNavigation()).AddSingleton<IJSRuntime>(new NoJs())
            .AddSingleton<AuroraInterop>().AddSingleton<UserClock>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var page = await renderer.RenderComponentAsync<global::Parrot.Web.Components.Pages.Dashboard.Notifications.Notifications>();
            return page.ToHtmlString();
        });
    }

    private sealed class ControlledNavigation : NavigationManager
    {
        public ControlledNavigation() => Initialize("http://localhost/", "http://localhost/dashboard/notifications");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering has no browser.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
    private sealed class ControlledFeed(IReadOnlyList<AppNotification> items, bool failRead = false) : INotificationFeed
    {
        public bool Available => true;
        public int ReadMutations { get; private set; }
        public event Action? Changed { add { } remove { } }
        public Task<IReadOnlyList<AppNotification>> ReadAsync(CancellationToken ct = default) => failRead ? throw new HttpRequestException("Controlled read failure") : Task.FromResult(items);
        public Task MarkReadAsync(string? id, CancellationToken ct = default) { ReadMutations++; return Task.CompletedTask; }
    }
}
