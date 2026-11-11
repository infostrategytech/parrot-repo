namespace Parrot.Web.Features.Dashboard.TaskRequests;

public sealed record HandoverCandidate(TaskPerson Person, bool Active, bool CanHandleTasks);
public sealed record CustomerHandover(string EventId, TaskCustomer Customer, string AgentId, string AgentName,
    string Summary, string? AssigneeId = null, string? ExistingTaskId = null);

/// <summary>Frontend contract for agent-generated human handovers. Production must create/reuse and assign the
/// task atomically and idempotently, then emit one recipient notification. The notification UI never creates tasks.</summary>
public interface ICustomerHandoverTasks
{
    bool Available { get; }
    Task<TaskRequest> CreateAsync(CustomerHandover handover, CancellationToken ct = default);
}
public sealed class UnavailableCustomerHandoverTasks : ICustomerHandoverTasks
{
    public bool Available => false;
    public Task<TaskRequest> CreateAsync(CustomerHandover handover, CancellationToken ct = default) => throw new NotSupportedException("Agent handovers need the tasks API.");
}
public static class HandoverAssignmentRules
{
    /// <summary>Use the designated eligible teammate, the sole eligible teammate, or the real owner when no
    /// eligible teammates exist. Never choose a random person when routing for a larger team is unspecified.</summary>
    public static TaskPerson Choose(TaskPerson owner, IEnumerable<HandoverCandidate> candidates, string? designatedId)
    {
        if (string.IsNullOrWhiteSpace(owner.Id)) throw new InvalidOperationException("The workspace owner is required before assigning a handover.");
        var team = candidates.Where(item => item.Active && item.CanHandleTasks && item.Person.Id != owner.Id).Select(item => item.Person).DistinctBy(item => item.Id).ToArray();
        if (team.Length == 0) return owner;
        if (designatedId is not null) return team.FirstOrDefault(person => person.Id == designatedId)
            ?? throw new InvalidOperationException("Choose an active teammate allowed to handle this task.");
        return team.Length == 1 ? team[0] : throw new InvalidOperationException("Choose the responsible teammate before assigning this handover.");
    }
}
