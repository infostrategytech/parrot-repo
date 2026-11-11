namespace Parrot.Web.Features.Dashboard.TaskRequests;

/// <summary>
/// What a task is about (designer, 20 October 2026). It picks the icon, the words and which details
/// the panel shows; it never changes how a task moves: every kind is opened, worked and closed the same way.
/// </summary>
public enum TaskKind { Order, Quote, Booking, Issue, CallBack, Other }

/// <summary>
/// Where a task has got to. Open and InProgress need the business; Waiting is on the customer.
/// Done and Cancelled are closed.
/// </summary>
public enum TaskState { Open, InProgress, Waiting, Done, Cancelled }

/// <summary>
/// A line of what was ordered or asked for, with the customer's note for that line; the price is optional.
/// A charge (designer, 21 October 2026) is an amount added on top, not an item: a delivery fee, a
/// commission, packaging. It has no quantity worth showing and counts towards the total like an item.
/// </summary>
public sealed record TaskLine(string Name, int Quantity, decimal? Price = null, string? Note = null, bool Charge = false);

/// <summary>
/// Something that happened to a task, said as a sentence by who did it ("Kora raised it…", "Adaeze moved
/// it to In progress"), or an update a person posted (its words in <see cref="Update"/>).
/// </summary>
public sealed record TaskEvent(DateTime At, string What, string Who, string? Update = null);

/// <summary>
/// The customer a task is for. From a conversation (it has one, and the task opens it), or someone the
/// Inbox doesn't know: a walk-in or a phone call, with a name and a number.
/// </summary>
public sealed record TaskCustomer(string Name, string Channel, string? Contact = null, string? ConversationId = null);

/// <summary>Something the business sells, to add to a task with its price: from Products & Services.</summary>
public sealed record TaskItem(string Name, decimal? Price, string? CurrencyCode = null);

/// <summary>A person on the team: who a task is assigned to.</summary>
public sealed record TaskPerson(string Id, string Name);

/// <summary>
/// One thing someone has to do for the business: raised by an agent from a conversation, or added by a
/// person. The title says the action ("Send Kemi's Kitchen a quote…"); what's needed says why. Everything
/// a person can enter is also editable afterwards, as people and agents both make mistakes.
/// </summary>
public sealed class TaskRequest
{
    public required string Id { get; init; }
    public required TaskKind Kind { get; set; }
    public required string Title { get; set; }

    /// <summary>What's needed, in a sentence or two: the agent's summary, or the rest of what a person typed.</summary>
    public string? Summary { get; set; }

    public TaskCustomer? Customer { get; set; }

    /// <summary>Where: a delivery address, or where a booking or a job is.</summary>
    public string? Address { get; set; }

    /// <summary>The agent or the person who raised it.</summary>
    public required string RaisedBy { get; init; }

    /// <summary>The agent's id, so the task shows the look the business chose for it.</summary>
    public string? RaisedById { get; init; }
    public bool RaisedByAgent { get; init; }
    /// <summary>Optional agent handover attached to this task; preserves an existing task's original creator/type.</summary>
    public string? HandoverEventId { get; set; }
    public string? HandoverAgentId { get; set; }
    public string? HandoverAgentName { get; set; }
    public required DateTime RaisedAt { get; init; }

    /// <summary>
    /// The line in the conversation that says the task was raised ("Kora raised a task from this
    /// conversation"). Reply opens the conversation there, with what led to it above (designer, 21 October
    /// 2026: a task comes from a run of messages, so no one message stands for it).
    /// </summary>
    public string? RaisedInMessageId { get; init; }

    /// <summary>Who is on it; null for nobody yet. Only shown once the business has a team.</summary>
    public TaskPerson? Assignee { get; set; }

    public TaskState State { get; set; } = TaskState.Open;
    public bool Urgent { get; set; }

    /// <summary>When it's due (UTC). Without a time it's due by the end of that day.</summary>
    public DateTime? DueAt { get; set; }
    public bool DueHasTime { get; set; }

    public List<TaskLine> Lines { get; set; } = [];

    /// <summary>The total: the lines' prices added up, or an amount typed when there are no prices.</summary>
    public decimal? Total { get; set; }
    /// <summary>Saved with the amounts. Null means legacy NGN, not the current workspace default.</summary>
    public string? CurrencyCode { get; set; }

    public List<TaskEvent> History { get; init; } = [];
    public DateTime LastActivityAt { get; set; }

    public bool IsClosed => State is TaskState.Done or TaskState.Cancelled;
    public bool IsOpen => !IsClosed;

    /// <summary>The business has to act (not waiting on the customer, not closed).</summary>
    public bool NeedsAction => State is TaskState.Open or TaskState.InProgress;
}

/// <summary>
/// What a person enters for a task: when adding one, and when editing one. One shape for both, so editing
/// offers exactly the fields adding does.
/// </summary>
public sealed record TaskDraft(
    TaskKind Kind,
    string Title,
    string? Summary = null,
    TaskCustomer? Customer = null,
    string? Address = null,
    IReadOnlyList<TaskLine>? Lines = null,
    decimal? Total = null,
    bool Urgent = false,
    DateTime? DueAt = null,
    bool DueHasTime = false,
    TaskPerson? Assignee = null,
    string? CurrencyCode = null);
