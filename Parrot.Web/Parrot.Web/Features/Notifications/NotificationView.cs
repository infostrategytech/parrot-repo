using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Features.Notifications;

public enum NotificationView { NeedsYou, Everything }

/// <summary>Shared by the panel and history page. Filter state is navigable, not a second inbox state.</summary>
public static class NotificationViews
{
    public static NotificationView Parse(string? value) => string.Equals(value, "everything", StringComparison.OrdinalIgnoreCase) ? NotificationView.Everything : NotificationView.NeedsYou;
    public static bool ParseUnread(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    public static string Href(NotificationView view, bool unread = false) => $"/dashboard/notifications?view={(view == NotificationView.Everything ? "everything" : "needs-you")}" + (unread ? "&unread=true" : "");
    public static IReadOnlyList<NotificationCard> Filter(IReadOnlyList<NotificationCard> cards, NotificationView view, bool unread = false) =>
        cards.Where(card => (view == NotificationView.Everything || card.NeedsYou) && (!unread || card.Unread)).ToArray();
    public static string NeedsYouDescription(int count) => count == 1 ? "1 notification needs your attention." : $"{count} notifications need your attention.";
    // The rail's badge counts every unread row, but it turns red only while one that needs you is unread; routine
    // updates keep the brand colour, like Inbox and Tasks, so red stays rare (designer, 5 November 2026).
    public static int UnreadNeedingYou(IReadOnlyList<NotificationCard> cards) => cards.Count(card => card.Items.Any(item => item.Unread && item.NeedsYou));
    public static string BadgeMeaning(int unread, int unreadNeedingYou) =>
        unreadNeedingYou == 0 ? "unread notifications"
        : unreadNeedingYou >= unread ? (unread == 1 ? "unread notification that needs you" : "unread notifications that need you")
        : unreadNeedingYou == 1 ? "unread notifications, 1 needs you"
        : $"unread notifications, {unreadNeedingYou} need you";
}

public static class NotificationDisplay
{
    public static IReadOnlyList<NotificationListItem> Rows(IReadOnlyList<NotificationCard> cards, bool sections, DateTimeOffset now, TimeZoneInfo? zone = null) => cards.Select(card => Row(card, sections, now, zone)).ToArray();
    private static NotificationListItem Row(NotificationCard card, bool sections, DateTimeOffset now, TimeZoneInfo? zone)
    {
        var item = card.Latest;
        var resolved = item.Kind == NotificationKind.Action && item.ResolvedAt is not null;
        var meta = resolved ? $"{item.Place} · {NotificationCatalogue.ResolutionLabel(item)} · {NotificationWords.When(item.ResolvedAt!.Value, now, zone)}" : $"{item.Place} · {NotificationWords.When(card.At, now, zone)}";
        var definition = NotificationCatalogue.Find(item.Type);
        var action = !card.NeedsYou ? null : card.Grouped ? definition?.ManyAction ?? (item.Type == NotificationType.AgentQuestion ? "View questions" : "Review actions") : NotificationCatalogue.ActionLabel(item);
        // A group of one type says which is newest, so the row still tells you something: "Check Damilola's transfer and 3 more".
        var detail = !card.Grouped ? (string.IsNullOrWhiteSpace(item.Detail) ? NotificationCatalogue.OutcomeText(item) : item.Detail)
            : definition?.GroupByType == true && !string.IsNullOrWhiteSpace(item.Detail) ? $"{item.Detail.Trim().TrimEnd('.')} and {card.Items.Count - 1} more" : null;
        // Headlines always come from the approved catalogue. A task's instructions are supporting
        // context, never a replacement title. Shared here so the page and popover cannot drift.
        var successful = resolved && item.Resolution is NotificationResolution.Completed or NotificationResolution.Answered or NotificationResolution.Approved or NotificationResolution.Repaired;
        var glyph = resolved ? successful ? IconName.RiCheckboxCircleFill : item.Resolution switch
        {
            NotificationResolution.Declined or NotificationResolution.Cancelled => IconName.RiCloseFill,
            NotificationResolution.WaitingOnCustomer => IconName.RiTimeFill,
            _ => IconName.RiInformationFill
        } : item.Type switch
        {
            NotificationType.KnowledgeReview => IconName.RiBookOpenFill,
            NotificationType.HumanHandoverTask => IconName.RiUserSharedFill,
            _ => Glyph(item.Mark)
        };
        var tone = resolved ? successful ? NotificationListTone.Success : NotificationListTone.Neutral : Tone(item);
        return new(card.Id, card.Title, meta, glyph, tone, detail, action, card.Unread, resolved, sections ? NotificationWords.Day(card.At, now, zone) : null);
    }
    private static IconName Glyph(NotificationMark mark) => mark switch { NotificationMark.Chat => IconName.RiChat3Fill, NotificationMark.Waiting or NotificationMark.Late => IconName.RiTimeFill, NotificationMark.Task => IconName.RiClipboardFill, NotificationMark.Booking => IconName.RiCalendarCheckFill, NotificationMark.Payment => IconName.RiBankCardFill, NotificationMark.Problem => IconName.RiErrorWarningFill, NotificationMark.Team => IconName.RiTeamFill, NotificationMark.Security => IconName.RiLock2Fill, _ => IconName.RiRobot3Fill };
    private static NotificationListTone Tone(AppNotification item) => item.Type == NotificationType.KnowledgeReview ? NotificationListTone.Brand : item.Mark switch
    {
        NotificationMark.Late or NotificationMark.Problem => NotificationListTone.Amber,
        NotificationMark.Booking or NotificationMark.Payment => NotificationListTone.Emerald,
        NotificationMark.Agent => NotificationListTone.Brand,
        _ => NotificationListTone.Neutral
    };
}
