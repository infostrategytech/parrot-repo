namespace Parrot.Web.Features.Dashboard.TaskRequests;

/// <summary>
/// A question for a teammate inside a task (designer, 15 November 2026: keep it simple). The person who asks keeps the
/// task; the person asked answers in one box; the answer comes back to the asker, who puts it in the reply to the customer.
/// Waiting, then answered, then closed once the asker has used it. Never changes the task's assignee, status or money.
/// </summary>
public sealed record TaskQuestion(string Id, string TaskId, string ConversationId, string Text,
    TaskPerson AskedBy, TaskPerson AskedOf, int Revision, DateTime UpdatedAt, string? Answer = null, bool Closed = false)
{
    public bool Answered => Answer is not null;

    /// <summary>It is this person's move: asked and not answered yet, or answered and not yet used by whoever asked.</summary>
    public bool Waits(string personId) => !Closed && (Answered ? AskedBy.Id == personId : AskedOf.Id == personId);
}

/// <summary>Someone a task can be passed to, with their role (the "department" a small business thinks in).</summary>
public sealed record QuestionPerson(TaskPerson Person, string? Role, bool CanReply);

public enum QuestionAction { Ask, Answer, Close }

public sealed record QuestionCommand(Guid RequestId, QuestionAction Action, string TaskId,
    string? QuestionId = null, int Revision = 0, string? Text = null, string? PersonId = null);

public static class TaskQuestionRules
{
    public const int MaxLength = 1000;

    public static string? Validate(QuestionCommand command) =>
        command.RequestId == Guid.Empty ? "Try that again."
        : string.IsNullOrWhiteSpace(command.TaskId) ? "Open the task again."
        : command.Action == QuestionAction.Ask && string.IsNullOrWhiteSpace(command.PersonId) ? "Choose who to ask."
        : command.Action == QuestionAction.Ask && string.IsNullOrWhiteSpace(command.Text) ? "Write your question."
        : command.Action == QuestionAction.Answer && string.IsNullOrWhiteSpace(command.Text) ? "Write your answer."
        : command.Text?.Trim().Length > MaxLength ? "Keep it under 1,000 characters."
        : command.Action != QuestionAction.Ask && (string.IsNullOrWhiteSpace(command.QuestionId) || command.Revision < 1) ? "Refresh and try again."
        : null;
}

/// <summary>Workspace/session scoped. Production must enforce permissions, revisions, idempotency and notify the other person.</summary>
public interface ITaskQuestions
{
    bool Available { get; }
    event Action? Changed;
    Task<IReadOnlyList<TaskQuestion>> ListAsync(CancellationToken ct = default);
    /// <summary>Who this task can be passed to or asked about: active teammates with access to Tasks and its channel.</summary>
    Task<IReadOnlyList<QuestionPerson>> PeopleAsync(string taskId, CancellationToken ct = default);
    Task<TaskQuestion> ApplyAsync(QuestionCommand command, CancellationToken ct = default);
}

public sealed class UnavailableTaskQuestions : ITaskQuestions
{
    public bool Available => false;
    public event Action? Changed { add { } remove { } }
    public Task<IReadOnlyList<TaskQuestion>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TaskQuestion>>([]);
    public Task<IReadOnlyList<QuestionPerson>> PeopleAsync(string taskId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<QuestionPerson>>([]);
    public Task<TaskQuestion> ApplyAsync(QuestionCommand command, CancellationToken ct = default) => throw new NotSupportedException("Questions need backend support.");
}
