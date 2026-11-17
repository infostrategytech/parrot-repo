namespace Parrot.Web.Features.Dashboard.TaskRequests;

/// <summary>Who can be given a task, the words after a change, and a safe Undo: shared by the table's Assignee menu,
/// Take it and Pass on. Server-side task/channel/role permissions remain authoritative.</summary>
public static class TaskAssignmentRules
{
    public static string Confirmation(TaskPerson? person, string viewerId) => person is null
        ? "Unassigned." : person.Id == viewerId ? "It's yours." : $"Given to {person.Name}.";

    public static async Task<bool> TryUndoAsync(ITaskStore store, string taskId, TaskPerson? expected, TaskPerson? before)
    {
        var current = (await store.ListAsync()).FirstOrDefault(t => t.Id == taskId);
        if (current is null || current.Assignee?.Id != expected?.Id) return false;
        await store.AssignAsync(taskId, before);
        return true;
    }

    public static async Task<IReadOnlyList<QuestionPerson>> PeopleAsync(ITaskStore store, ITaskQuestions questions, string taskId)
    {
        // An available permission-aware service returning nobody is not a reason to offer the full roster.
        var people = questions.Available ? await questions.PeopleAsync(taskId)
            : (await store.TeamAsync()).Select(p => new QuestionPerson(p, null, false)).ToArray();
        return people.Where(p => !string.IsNullOrWhiteSpace(p.Person.Id)).DistinctBy(p => p.Person.Id).ToArray();
    }
}
