namespace Parrot.Domain.Entities;

/// <summary>What a task is about. It picks the icon and the words; every kind is worked the same way.</summary>
public enum WorkTaskKind { Order, Quote, Booking, Issue, CallBack, Other }

/// <summary>Where a task has got to. Open and InProgress need the business, Waiting the customer; Done and Cancelled are closed.</summary>
public enum WorkTaskState { Open, InProgress, Waiting, Done, Cancelled }

/// <summary>
/// One thing someone has to do for a customer: raised by an agent from a
/// conversation, or added by a person. Called a work task so it never reads
/// as <see cref="System.Threading.Tasks.Task"/>.
/// </summary>
public class WorkTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The workspace it belongs to. Every lookup is scoped by it.</summary>
    public required string WorkspaceId { get; set; }

    /// <summary>The workspace's own count, 1 up: how people name it (T-12) and how its address reads.</summary>
    public int Number { get; set; }

    public WorkTaskKind Kind { get; set; }

    public required string Title { get; set; }

    /// <summary>What's needed: the agent's summary, or the rest of what a person typed.</summary>
    public string? Summary { get; set; }

    /// <summary>
    /// The customer's name as it was when the task was raised or last edited.
    /// Null when the task is for nobody in particular.
    /// </summary>
    public string? CustomerName { get; set; }

    /// <summary>whatsapp, instagram, facebook or tiktok; empty for a walk-in or a phone call.</summary>
    public string? CustomerChannel { get; set; }

    /// <summary>A number or @handle to reach them by.</summary>
    public string? CustomerContact { get; set; }

    /// <summary>
    /// The conversation it came from, as the connection and the customer's
    /// sender id. Both null for a customer the Inbox doesn't know. The
    /// connection is also what limits a task to the team members whose
    /// channels reach it.
    /// </summary>
    public Guid? ConnectionId { get; set; }

    public string? CustomerSender { get; set; }

    /// <summary>Where: a delivery address, or where a booking or a job is.</summary>
    public string? Address { get; set; }

    /// <summary>The agent's or the person's name, as it was then.</summary>
    public required string RaisedBy { get; set; }

    /// <summary>The agent's id, or the person's user id.</summary>
    public string? RaisedById { get; set; }

    public bool RaisedByAgent { get; set; }

    public DateTime RaisedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The message in the conversation where the agent raised it, for Reply to open the thread there.</summary>
    public string? RaisedInMessageId { get; set; }

    /// <summary>Who is on it, by user id; null for nobody yet.</summary>
    public string? AssigneeId { get; set; }

    /// <summary>Their name when it was given to them, so a task still says who had it after they leave the team.</summary>
    public string? AssigneeName { get; set; }

    public WorkTaskState State { get; set; } = WorkTaskState.Open;

    public bool Urgent { get; set; }

    /// <summary>When it's due (UTC). Without a time it's due by the end of that day.</summary>
    public DateTime? DueAt { get; set; }

    public bool DueHasTime { get; set; }

    /// <summary>What was ordered or asked for, copied in: a later price change never rewrites an old order.</summary>
    public List<WorkTaskLine> Lines { get; set; } = [];

    /// <summary>The lines' prices added up, or an amount typed when there are no prices.</summary>
    public decimal? Total { get; set; }

    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>When it was marked done or cancelled; null while open.</summary>
    public DateTime? ClosedAt { get; set; }

    public List<WorkTaskEvent> History { get; set; } = [];

    public bool IsClosed => State is WorkTaskState.Done or WorkTaskState.Cancelled;
}

/// <summary>
/// A line of what was ordered or asked for. A charge is an amount on top
/// (a delivery fee, packaging): it counts towards the total like an item.
/// </summary>
public sealed record WorkTaskLine(string Name, int Quantity, decimal? Price = null, string? Note = null, bool Charge = false);

/// <summary>What a history entry records. The screens put it into words.</summary>
public static class WorkTaskActions
{
    /// <summary>An agent raised it from a conversation.</summary>
    public const string Raised = "raised";

    /// <summary>A person added it.</summary>
    public const string Added = "added";

    public const string Edited = "edited";

    /// <summary>Value: the state it moved to.</summary>
    public const string State = "state";

    /// <summary>Value: the assignee's name; TargetId: their user id.</summary>
    public const string Assigned = "assigned";

    public const string Unassigned = "unassigned";

    /// <summary>Value: "set" or "cleared".</summary>
    public const string Due = "due";

    /// <summary>Value: "on" or "off".</summary>
    public const string Urgent = "urgent";

    /// <summary>An internal note for the team, in Note.</summary>
    public const string Note = "note";
}

/// <summary>
/// Something that happened to a task, written by the server when the change
/// is saved, so the trail is what really happened rather than what a screen sent.
/// </summary>
public class WorkTaskEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TaskId { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>One of <see cref="WorkTaskActions"/>.</summary>
    public required string Action { get; set; }

    /// <summary>The agent's or the person's name, as it was then.</summary>
    public required string Who { get; set; }

    public string? WhoId { get; set; }

    public string? Value { get; set; }

    public string? TargetId { get; set; }

    public string? Note { get; set; }
}
