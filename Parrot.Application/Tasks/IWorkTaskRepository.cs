using Parrot.Domain.Entities;

namespace Parrot.Application.Tasks;

public interface IWorkTaskRepository
{
    /// <summary>
    /// Every open task in the workspace, and the latest <paramref name="closedLimit"/>
    /// closed ones, each with its history.
    /// </summary>
    /// <param name="connections">
    /// For a team member limited to some channels: tasks from conversations on
    /// other channels are left out. Tasks with no conversation are always in.
    /// Null is every connection.
    /// </param>
    Task<List<WorkTask>> ListAsync(string workspaceId, IReadOnlySet<Guid>? connections, int closedLimit, CancellationToken ct);

    /// <summary>
    /// Saves a new task with its first history entry, numbering it next in its
    /// workspace. Two tasks added at once never share a number.
    /// </summary>
    Task AddAsync(WorkTask task, CancellationToken ct);

    /// <summary>
    /// Loads the task, applies <paramref name="change"/> and saves it with the
    /// history entry the change returns, all in one go. Null when there is no
    /// such task. Whatever <paramref name="change"/> throws is thrown, and nothing is saved.
    /// </summary>
    Task<WorkTask?> ChangeAsync(string workspaceId, int number, Func<WorkTask, WorkTaskEvent?> change, CancellationToken ct);
}
