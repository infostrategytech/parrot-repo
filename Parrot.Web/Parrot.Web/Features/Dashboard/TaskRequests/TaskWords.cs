using System.Globalization;
using System.Text.RegularExpressions;
using Parrot.Domain.Entities;
using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;

namespace Parrot.Web.Features.Dashboard.TaskRequests;

/// <summary>How tasks read on screen: the words, icons and tones for kinds and states, and times said plainly.</summary>
public static partial class TaskWords
{
    public static readonly TaskKind[] Kinds = [TaskKind.Order, TaskKind.Quote, TaskKind.Booking, TaskKind.Issue, TaskKind.CallBack, TaskKind.Other];

    /// <summary>The states a person can move a task to, in the order the menu lists them.</summary>
    public static readonly TaskState[] States = [TaskState.Open, TaskState.InProgress, TaskState.Waiting, TaskState.Done, TaskState.Cancelled];

    public static string Label(TaskKind kind) => kind switch
    {
        TaskKind.Order => "Order",
        TaskKind.Quote => "Quote",
        TaskKind.Booking => "Booking",
        TaskKind.Issue => "Refund or complaint",
        TaskKind.CallBack => "Call back",
        _ => "Other",
    };

    public static string Describe(TaskKind kind) => kind switch
    {
        TaskKind.Order => "An order to confirm, pack or send",
        TaskKind.Quote => "A price to work out and send",
        TaskKind.Booking => "A time to agree with the customer",
        TaskKind.Issue => "A refund, a return or something gone wrong",
        TaskKind.CallBack => "A customer wants to hear from a person",
        _ => "Anything else that needs doing",
    };

    public static IconName Icon(TaskKind kind) => kind switch
    {
        TaskKind.Order => IconName.RiShoppingBag3Fill,
        TaskKind.Quote => IconName.RiPriceTag3Fill,
        TaskKind.Booking => IconName.RiCalendarCheckFill,
        TaskKind.Issue => IconName.RiRefund2Fill,
        TaskKind.CallBack => IconName.RiPhoneFill,
        _ => IconName.RiTaskFill,
    };

    /// <summary>The modifier that tints a kind's icon, so a list is easy to scan.</summary>
    public static string Tint(TaskKind kind) => "is-" + kind.ToString().ToLowerInvariant();

    /// <summary>What the items are called, for the kinds that have any.</summary>
    public static string DetailsTitle(TaskKind kind) => kind switch
    {
        TaskKind.Order => "What they ordered",
        TaskKind.Quote => "What they asked for",
        TaskKind.Issue => "What it's about",
        _ => "Items",
    };

    /// <summary>
    /// The type, guessed from what a person typed (designer, 20 October 2026: adding a task is one
    /// sentence). Plain word rules, so it works with no AI; the person can change it with one tap.
    /// </summary>
    public static TaskKind Guess(string text)
    {
        var words = text.ToLowerInvariant();
        if (IssueWords().IsMatch(words)) return TaskKind.Issue;
        if (QuoteWords().IsMatch(words)) return TaskKind.Quote;
        if (BookingWords().IsMatch(words)) return TaskKind.Booking;
        if (CallWords().IsMatch(words)) return TaskKind.CallBack;
        if (OrderWords().IsMatch(words)) return TaskKind.Order;
        return TaskKind.Other;
    }

    [GeneratedRegex(@"\b(refund|return|complain|complaint|wrong (size|item|order)|damaged|broken|faulty|exchange|replace)")]
    private static partial Regex IssueWords();
    [GeneratedRegex(@"\b(quote|quotation|estimate|price list|how much)")]
    private static partial Regex QuoteWords();
    [GeneratedRegex(@"\b(book|booking|appointment|fitting|reserv|schedule|session)")]
    private static partial Regex BookingWords();
    [GeneratedRegex(@"\b(call|ring|phone|get back to)")]
    private static partial Regex CallWords();
    [GeneratedRegex(@"\b(order|deliver|delivery|pack|ship|dispatch|send out|pick ?up|pay|payment|transfer|receipt|invoice)")]
    private static partial Regex OrderWords();

    /// <summary>
    /// What a person typed, as a title and the rest: the first sentence (or line) is the title, so one box
    /// is enough. "Send Kemi a quote. She needs it today." → "Send Kemi a quote" and "She needs it today."
    /// </summary>
    public static (string Title, string? More) Split(string text)
    {
        var clean = text.Trim();
        var end = FirstBreak().Match(clean);
        if (!end.Success) return (clean.TrimEnd('.'), null);
        var title = clean[..end.Index].Trim().TrimEnd('.');
        var rest = clean[(end.Index + end.Length)..].Trim();
        return (title, rest.Length == 0 ? null : rest);
    }

    [GeneratedRegex(@"(\r?\n)+|(?<=[.!?])\s+(?=[A-Z0-9₦])")]
    private static partial Regex FirstBreak();

    public static string Label(TaskState state) => state switch
    {
        TaskState.Open => "New",
        TaskState.InProgress => "In progress",
        TaskState.Waiting => "Waiting on customer",
        TaskState.Done => "Done",
        _ => "Cancelled",
    };

    public static string Describe(TaskState state) => state switch
    {
        TaskState.Open => "Nobody has started it yet",
        TaskState.InProgress => "Someone is on it",
        TaskState.Waiting => "Waiting for the customer to reply",
        TaskState.Done => "Finished",
        _ => "Not going ahead",
    };

    public static StatusTone Tone(TaskState state) => state switch
    {
        TaskState.Open => StatusTone.Info,
        TaskState.InProgress => StatusTone.Brand,
        TaskState.Waiting => StatusTone.Warning,
        TaskState.Done => StatusTone.Success,
        _ => StatusTone.Neutral,
    };

    /// <summary>The modifier that colours a state's dot: the same tones as its badge.</summary>
    public static string ToneClass(TaskState state) => "is-" + Tone(state).ToString().ToLowerInvariant();

    /// <summary>
    /// The look the business chose for the agent that raised a task (designer, 20 October 2026: it helps
    /// to recognise who raised it at a glance). Found by the agent's id, then its name; an agent that
    /// isn't set up here keeps one look of its own. Null for a task a person added.
    /// </summary>
    public static string? AgentLook(TaskRequest task, IEnumerable<AiAgent> agents)
    {
        if (!task.RaisedByAgent) return null;
        var list = agents.ToList();
        var agent = list.FirstOrDefault(a => task.RaisedById is not null && a.Id == task.RaisedById)
            ?? list.FirstOrDefault(a => string.Equals(a.Name, task.RaisedBy, StringComparison.OrdinalIgnoreCase));
        var mascot = agent?.Mascot ?? AgentBuilderCopy.Mascots[task.RaisedBy.Sum(c => c) % AgentBuilderCopy.Mascots.Count];
        return AgentBuilderCopy.MascotImage(mascot);
    }

    /// <summary>
    /// What a history entry says happened, to follow who did it: "Kora raised it from Kemi's Kitchen's
    /// conversation", "Adaeze moved it to In progress". The server records the change; the words are here.
    /// </summary>
    public static string What(Parrot.Application.Tasks.TaskEventResponse entry) => entry.Action switch
    {
        WorkTaskActions.Raised => entry.Value is { Length: > 0 } customer ? $"Raised it from {customer}'s conversation" : "Raised it",
        WorkTaskActions.Added => "Added it",
        WorkTaskActions.Edited => "Edited the details",
        WorkTaskActions.State => Enum.TryParse<TaskState>(entry.Value, out var state)
            ? state switch
            {
                TaskState.Done => "Marked it done",
                TaskState.Cancelled => "Cancelled it",
                _ => $"Moved it to {Label(state)}",
            }
            : "Changed the status",
        WorkTaskActions.Assigned => entry.TargetId is not null && entry.TargetId == entry.WhoId ? "Took it on" : $"Gave it to {entry.Value}",
        WorkTaskActions.Unassigned => "Unassigned it",
        WorkTaskActions.Due => entry.Value == "cleared" ? "Took the due date off" : "Changed the due date",
        WorkTaskActions.Urgent => entry.Value == "off" ? "Took Urgent off" : "Marked it urgent",
        WorkTaskActions.Note => "Added a note",
        _ => "Changed it",
    };

    /// <summary>
    /// A task for this customer: raised from one of their conversations ("{connection}:{sender}"), or, for a
    /// customer the Inbox doesn't know (<paramref name="unlinkedName"/>), added for them by name.
    /// </summary>
    public static bool IsFor(TaskRequest task, IReadOnlyCollection<string> conversations, string? unlinkedName = null) =>
        task.Customer is { } customer && (customer.ConversationId is { } id
            ? conversations.Contains(id)
            : unlinkedName is not null && string.Equals(customer.Name, unlinkedName, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Raised by Kora" for an agent, "Added by Adaeze" for a person.</summary>
    public static string RaisedBy(TaskRequest task) => (task.RaisedByAgent ? "Raised by " : "Added by ") + task.RaisedBy;

    /// <summary>How long ago, the way people say it: "3h ago", "Yesterday", "2 weeks ago" (by the person's own calendar).</summary>
    public static string Ago(DateTime at, DateTime now, TimeZoneInfo zone)
    {
        var gap = now - at;
        if (gap < TimeSpan.FromMinutes(2)) return "Just now";
        if (gap < TimeSpan.FromHours(1)) return $"{(int)gap.TotalMinutes} min ago";
        if (gap < TimeSpan.FromHours(24)) return $"{(int)gap.TotalHours}h ago";
        var days = (Local(now, zone).Date - Local(at, zone).Date).Days;
        if (days <= 1) return "Yesterday";
        if (days < 14) return $"{days} days ago";
        return $"{days / 7} weeks ago";
    }

    /// <summary>
    /// When it's due, and how much that matters: late is a problem (red), due today a heads-up (amber), and a time
    /// today that has passed is late (designer, 29 October 2026). "Today, 5pm",
    /// "Tomorrow", "Thursday, 11am", "1 day late". By the person's own calendar, not the server's.
    /// </summary>
    public static (string Text, StatusTone? Tone) Due(DateTime? dueAt, bool hasTime, DateTime now, TimeZoneInfo zone)
    {
        if (dueAt is not { } due) return ("No date", null);
        var local = Local(due, zone);
        var days = (local.Date - Local(now, zone).Date).Days;
        var at = hasTime ? ", " + Hour(local) : "";
        var late = hasTime && due < now;
        return days switch
        {
            < -1 => ($"{-days} days late", StatusTone.Danger),
            -1 => ("1 day late", StatusTone.Danger),
            0 => ("Today" + at, late ? StatusTone.Danger : StatusTone.Warning),
            1 => ("Tomorrow" + at, null),
            < 7 => (local.ToString("dddd", CultureInfo.InvariantCulture) + at, null),
            _ => (local.ToString("d MMM", CultureInfo.InvariantCulture) + at, null),
        };
    }

    /// <summary>"5pm", "11:30am".</summary>
    public static string Hour(DateTime local) =>
        (local.Minute == 0 ? local.ToString("%h", CultureInfo.InvariantCulture) : local.ToString("h:mm", CultureInfo.InvariantCulture))
        + (local.Hour < 12 ? "am" : "pm");

    private static DateTime Local(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    /// <summary>A day and an optional time, in the person's zone, as the moment it's due (UTC). Without a time: 6pm that day.</summary>
    public static DateTime DueMoment(DateOnly day, TimeOnly? time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(time ?? new TimeOnly(18, 0), DateTimeKind.Unspecified), zone);

    /// <summary>What a status change that didn't save was for: "Couldn't mark it done."</summary>
    public static string StateFailed(TaskState state, bool wasClosed) => state switch
    {
        TaskState.Done => "Couldn't mark it done.",
        TaskState.Cancelled => "Couldn't cancel it.",
        TaskState.Open when wasClosed => "Couldn't open it again.",
        _ => $"Couldn't move it to {Label(state)}.",
    };

    /// <summary>What an assignment that didn't save was for: "Couldn't give it to Chinedu."</summary>
    public static string AssignFailed(TaskPerson? person, string meId) =>
        person is null ? "Couldn't unassign it." : person.Id == meId ? "Couldn't give it to you." : $"Couldn't give it to {person.Name}.";

    public static string Naira(decimal amount) => "₦" + amount.ToString("#,0", CultureInfo.InvariantCulture);

    public static string Channel(string channel) => channel switch
    {
        "whatsapp" => "WhatsApp",
        "instagram" => "Instagram",
        "facebook" => "Facebook",
        "" => "",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(channel),
    };
}
