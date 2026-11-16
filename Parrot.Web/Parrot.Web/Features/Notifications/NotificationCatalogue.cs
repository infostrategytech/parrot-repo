namespace Parrot.Web.Features.Notifications;

public enum NotificationType
{
    Legacy, AgentQuestion, AgentApproval, KnowledgeReview, AgentRecovery, ImportFailed, HumanHandoverTask,
    TaskNeedsAttention, TaskAssigned, TaskDue, TaskOverdue, TaskOutcome, BookingConfirmed, BookingChanged,
    BookingCancelled, PaymentReceived, PaymentProblem, ConnectionAttention, ToolActionFailed, UsageNearLimit,
    UsageLimitReached, BillingPaymentFailed, SignInDetected, PasswordChanged, InvitationAccepted, TeamQuestionAsked, TeamQuestionAnswered
}

/// <summary>One notification type and its words. <paramref name="Many"/>: the headline for several of this type still waiting
/// (a {0} for the count), so repeats read as one row, as Instagram's "4 new messages" does, with
/// <paramref name="ManyAction"/> to open them (designer, 5 November 2026). Agent questions and approvals group per agent instead.</summary>
public sealed record NotificationDefinition(NotificationType Type, string Key, string Title, string ActionLabel, bool GroupByAgent = false,
    string? Many = null, string? ManyAction = null)
{
    public bool GroupByType => !GroupByAgent && Many is not null;
}

/// <summary>Business-neutral panel copy. Confirmed outcomes use source-provided facts, never guessed model text.
/// Type, source topic and Action/Update are separate; this registry does not confer permissions.</summary>
public static class NotificationCatalogue
{
    public static IReadOnlyList<NotificationDefinition> All { get; } =
    [
        new(NotificationType.AgentQuestion, "agent.question.pending", "Your agent has a question for you.", "Answer agent", true),
        new(NotificationType.AgentApproval, "agent.approval.pending", "Your agent needs your approval.", "Review action", true),
        new(NotificationType.KnowledgeReview, "knowledge.review.pending", "You have information to review.", "Review information"),
        new(NotificationType.AgentRecovery, "agent.recovery.required", "Your agent needs your attention.", "View agent"),
        new(NotificationType.ImportFailed, "background.import.failed", "Your import needs attention.", "Review import"),
        // The agent has already made the task and given it to someone; this tells that person (designer, 5 November 2026).
        new(NotificationType.HumanHandoverTask, "conversation.handover.task_created", "A customer has been assigned to you.", "Open task", Many: "{0} customers have been assigned to you.", ManyAction: "View tasks"),
        new(NotificationType.TaskNeedsAttention, "task.action_required", "A task needs your attention.", "Open task", Many: "{0} tasks need your attention.", ManyAction: "View tasks"),
        new(NotificationType.TaskAssigned, "task.assigned", "A task has been assigned to you.", "Open task", Many: "{0} tasks have been assigned to you.", ManyAction: "View tasks"),
        new(NotificationType.TeamQuestionAsked, "task.question.asked", "A teammate asked you a question.", "Open chat"),
        new(NotificationType.TeamQuestionAnswered, "task.question.answered", "Your question has an answer.", "Open chat"),
        new(NotificationType.TaskDue, "task.due", "A task is due soon.", "Open task", Many: "{0} tasks are due soon.", ManyAction: "View tasks"),
        new(NotificationType.TaskOverdue, "task.overdue", "A task is overdue.", "Open task", Many: "{0} tasks are overdue.", ManyAction: "View tasks"),
        new(NotificationType.TaskOutcome, "task.outcome.confirmed", "A task has an update.", "Open task"),
        new(NotificationType.BookingConfirmed, "booking.confirmed", "A booking is confirmed.", "View booking"),
        new(NotificationType.BookingChanged, "booking.changed", "A booking has changed.", "View booking"),
        new(NotificationType.BookingCancelled, "booking.cancelled", "A booking was cancelled.", "View booking"),
        new(NotificationType.PaymentReceived, "payment.received", "A payment was received.", "View payment"),
        new(NotificationType.PaymentProblem, "payment.problem", "A payment needs attention.", "Review payment", Many: "{0} payments need attention.", ManyAction: "Review payments"),
        new(NotificationType.ConnectionAttention, "connection.attention_required", "A connection needs attention.", "Review connection", Many: "{0} connections need attention.", ManyAction: "Review connections"),
        new(NotificationType.ToolActionFailed, "tool.action.failed", "Your agent couldn't complete an action.", "Review action"),
        new(NotificationType.UsageNearLimit, "billing.usage_near_limit", "You're close to your conversation limit.", "View plan"),
        new(NotificationType.UsageLimitReached, "billing.limit_reached", "Your conversation limit needs attention.", "View plan"),
        new(NotificationType.BillingPaymentFailed, "billing.payment_failed", "Your plan payment needs attention.", "Review billing"),
        new(NotificationType.SignInDetected, "security.signin_detected", "A new sign-in was detected.", "View security"),
        new(NotificationType.PasswordChanged, "security.password_changed", "Your password was changed.", "View security"),
        new(NotificationType.InvitationAccepted, "team.invitation_accepted", "A teammate accepted your invitation.", "View team"),
    ];
    public static NotificationDefinition? Find(NotificationType type) => All.FirstOrDefault(item => item.Type == type);
    public static bool IsInternalDestination(string? href) => !string.IsNullOrWhiteSpace(href) && href.StartsWith('/') &&
        !href.StartsWith("//", StringComparison.Ordinal) && !href.Contains('\\') && !href.Any(char.IsControl) &&
        (href.StartsWith("/dashboard/", StringComparison.Ordinal) || href.StartsWith("/ai-operations", StringComparison.Ordinal));
    public static string Title(AppNotification item, int count = 1)
    {
        var agent = string.IsNullOrWhiteSpace(item.AgentName) ? "Your agent" : item.AgentName.Trim();
        if (item.ResolvedAt is not null && item.Kind == NotificationKind.Action)
            return item.Type switch
            {
                NotificationType.AgentQuestion => $"Question from {agent} · {ResolutionLabel(item)}",
                NotificationType.TeamQuestionAsked => $"Question from your team · {ResolutionLabel(item)}",
                NotificationType.TeamQuestionAnswered => $"Answer from your team · {ResolutionLabel(item)}",
                NotificationType.AgentApproval => $"Action from {agent} · {ResolutionLabel(item)}",
                NotificationType.HumanHandoverTask or NotificationType.TaskAssigned or NotificationType.TaskNeedsAttention or NotificationType.TaskOverdue => $"Task · {ResolutionLabel(item)}",
                NotificationType.KnowledgeReview => $"Information review · {ResolutionLabel(item)}",
                _ => item.Text,
            };
        // Several of one type still waiting: one generic headline with the count.
        if (count > 1 && Find(item.Type) is { GroupByType: true, Many: { } many }) return string.Format(System.Globalization.CultureInfo.InvariantCulture, many, count);
        return item.Type switch
        {
            NotificationType.AgentQuestion => count > 1 ? $"{agent} has {count} questions that need your answers." : $"{agent} has a question for you.",
            NotificationType.AgentApproval => count > 1 ? $"{agent} has {count} actions that need your approval." : $"{agent} needs your approval.",
            NotificationType.KnowledgeReview => item.PendingCount > 1 ? $"You have {item.PendingCount} items to review." : "You have information to review.",
            NotificationType.AgentRecovery => $"{agent} needs your attention.",
            NotificationType.ToolActionFailed => $"{agent} couldn't complete an action.",
            NotificationType.Legacy => item.Text,
            // Every headline is the type's fixed one, outcomes too. An amount or a booking time is the second line's
            // (OutcomeText), so it never reaches a browser notification on a shared screen (designer, 5 November 2026).
            _ => Find(item.Type)?.Title ?? "An update is available in yára.",
        };
    }
    /// <summary>A confirmed outcome's own words ("Amaka paid ₦25,000 for her order"), shown under its fixed headline.</summary>
    public static string? OutcomeText(AppNotification item) => (item.Type is NotificationType.PaymentReceived or NotificationType.BookingConfirmed or
        NotificationType.BookingChanged or NotificationType.BookingCancelled or NotificationType.TaskOutcome or NotificationType.ConnectionAttention or
        NotificationType.InvitationAccepted) && !string.IsNullOrWhiteSpace(item.Text) ? item.Text.Trim() : null;
    /// <summary>Where a row opens. A group opens the list its items are in, not a picker (designer, 5 November 2026):
    /// tasks given to you open Tasks on Mine; anything else opens the page its items live on.</summary>
    public static string? Destination(NotificationCard card)
    {
        if (!card.Grouped) return card.Latest.Href;
        if (card.Latest.Type is NotificationType.TaskAssigned or NotificationType.HumanHandoverTask) return "/dashboard/tasks?view=mine";
        var href = card.Latest.Href;
        if (!IsInternalDestination(href)) return null;
        var parts = href!.Split('?', '#')[0].TrimEnd('/').Split('/');
        return string.Join('/', parts.Take(3)); // "/dashboard/tasks/OM-1?x" → "/dashboard/tasks"
    }
    public static string ActionLabel(AppNotification item) => item.Type == NotificationType.AgentQuestion && !string.IsNullOrWhiteSpace(item.AgentName)
        ? $"Answer {item.AgentName.Trim()}" : Find(item.Type)?.ActionLabel ?? "View details";
    public static string ResolutionLabel(AppNotification item) => item.Resolution switch
    {
        NotificationResolution.Answered => "Answered", NotificationResolution.Approved => "Approved",
        NotificationResolution.Declined => "Declined", NotificationResolution.Cancelled => "Cancelled",
        NotificationResolution.Withdrawn => "Withdrawn", NotificationResolution.Repaired => "Reconnected",
        NotificationResolution.Unavailable => "No longer available", NotificationResolution.WaitingOnCustomer => "Waiting on customer", _ => "Done",
    };
}

public sealed record NotificationCard(string Id, IReadOnlyList<AppNotification> Items)
{
    public AppNotification Latest => Items.OrderByDescending(item => item.At).First();
    public string Title => NotificationCatalogue.Title(Latest, Items.Count);
    public bool Unread => Items.Any(item => item.Unread);
    public bool NeedsYou => Items.Any(item => item.NeedsYou);
    public bool Grouped => Items.Count > 1;
    public string? Href => NotificationCatalogue.Destination(this);
    public DateTimeOffset At => Latest.At;
}

public static class NotificationProjection
{
    /// <summary>Group actual unresolved records, never event counts or names. Feed already enforces access;
    /// the scope keys additionally prevent projection from mixing workspaces/recipients. Missing IDs stay separate.</summary>
    public static IReadOnlyList<NotificationCard> Build(IEnumerable<AppNotification> source)
    {
        var unique = source.GroupBy(item => (item.WorkspaceId, item.RecipientId, item.Id)).Select(group => group.OrderByDescending(item => item.At).First()).ToArray();
        return unique.GroupBy(item => GroupKey(item) ?? ItemKey(item))
            // A type with only one waiting keeps that item's own id, so it reads and opens as itself.
            .Select(group => new NotificationCard(group.Key.StartsWith("type:", StringComparison.Ordinal) && group.Count() == 1 ? ItemKey(group.First()) : group.Key,
                group.OrderByDescending(item => item.At).ToArray()))
            .OrderByDescending(card => card.At).ToArray();
    }
    private static string ItemKey(AppNotification item) => $"item:{item.WorkspaceId}:{item.RecipientId}:{item.Id}";

    private static string? GroupKey(AppNotification item)
    {
        if (!item.NeedsYou || string.IsNullOrWhiteSpace(item.WorkspaceId) || string.IsNullOrWhiteSpace(item.RecipientId)) return null;
        var definition = NotificationCatalogue.Find(item.Type);
        if (definition?.GroupByAgent == true) return string.IsNullOrWhiteSpace(item.AgentId) ? null : $"group:{item.WorkspaceId}:{item.RecipientId}:{item.AgentId}:{item.Type}";
        // Same type, same person: "4 tasks need your attention" rather than four identical headlines.
        return definition?.GroupByType == true ? $"type:{item.WorkspaceId}:{item.RecipientId}:{item.Type}" : null;
    }
}
