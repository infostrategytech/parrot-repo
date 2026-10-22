namespace Parrot.Web.Features.Dashboard.TaskRequests;

/// <summary>
/// Where tasks are kept: agents raise them, people add and work them. The screens only talk to this,
/// so the demo's tasks and the backend's sit behind the same calls (tasks-backend-requests).
/// </summary>
public interface ITaskStore
{
    /// <summary>False until there is somewhere to keep tasks: the page says they are on the way.</summary>
    bool Available { get; }

    /// <summary>The person using the app, for the record and for "Mine".</summary>
    TaskPerson Me { get; }

    event Action? Changed;

    Task<IReadOnlyList<TaskRequest>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The team tasks can be assigned to. One person means no team: assignment stays hidden.</summary>
    Task<IReadOnlyList<TaskPerson>> TeamAsync(CancellationToken cancellationToken = default);

    /// <summary>The customers a new task can be for: the business's conversations.</summary>
    Task<IReadOnlyList<TaskCustomer>> CustomersAsync(CancellationToken cancellationToken = default);

    /// <summary>The business's items with their price, for "From Products & Services" (its own list).</summary>
    Task<IReadOnlyList<TaskItem>> ItemsAsync(CancellationToken cancellationToken = default);

    Task<TaskRequest> CreateAsync(TaskDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Changes what a person entered (title, type, what's needed, customer, where, items, total).</summary>
    Task EditAsync(string id, TaskDraft draft, CancellationToken cancellationToken = default);

    Task SetStateAsync(string id, TaskState state, CancellationToken cancellationToken = default);
    Task SetUrgentAsync(string id, bool urgent, CancellationToken cancellationToken = default);
    Task SetDueAsync(string id, DateTime? dueAt, bool hasTime, CancellationToken cancellationToken = default);
    Task AssignAsync(string id, TaskPerson? assignee, CancellationToken cancellationToken = default);

    /// <summary>An internal note for the team ("Called her, she'll pay on Friday"), kept in the task's history.</summary>
    Task PostUpdateAsync(string id, string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real mode until the backend keeps tasks: nothing to list, and nothing made up. Before this, every
/// workspace saw the same three sample requests, and a change one person made reached everyone.
/// </summary>
public sealed class UnavailableTaskStore : ITaskStore
{
    public bool Available => false;
    public TaskPerson Me { get; } = new("", "");
    public event Action? Changed { add { } remove { } }

    public Task<IReadOnlyList<TaskRequest>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TaskRequest>>([]);

    public Task<IReadOnlyList<TaskPerson>> TeamAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TaskPerson>>([]);

    public Task<IReadOnlyList<TaskCustomer>> CustomersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TaskCustomer>>([]);

    public Task<IReadOnlyList<TaskItem>> ItemsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TaskItem>>([]);

    public Task<TaskRequest> CreateAsync(TaskDraft draft, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task EditAsync(string id, TaskDraft draft, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task SetStateAsync(string id, TaskState state, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task SetUrgentAsync(string id, bool urgent, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task SetDueAsync(string id, DateTime? dueAt, bool hasTime, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task AssignAsync(string id, TaskPerson? assignee, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task PostUpdateAsync(string id, string text, CancellationToken cancellationToken = default) => throw Unavailable();

    private static NotSupportedException Unavailable() => new("Tasks need the tasks API.");
}
