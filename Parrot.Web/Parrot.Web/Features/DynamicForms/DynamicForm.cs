using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Features.DynamicForms;

/// <summary>
/// A form the AI generates (agent setup, business-knowledge topics), as the app draws it (designer, 27 October
/// 2026). Read from what the backend sends (<see cref="DynamicFormReader"/>, dynamic-form-schema.json) and drawn
/// by the shared QuestionForm; the answers go back as a FormResult (<see cref="DynamicFormWriter"/>). The package
/// sends what to ask, never how it looks.
/// </summary>
/// <param name="Id">Stable id for this step, sent back with the answers.</param>
/// <param name="Prefilled">Answers already known, by question id, shown to check with "Change".</param>
public sealed record DynamicForm(
    string Id,
    string Title,
    string? Subtitle,
    FormChanges? Changes,
    IReadOnlyList<FormQuestion> Questions,
    IReadOnlyDictionary<string, FormAnswer> Prefilled,
    string SubmitLabel,
    bool AllowDecide,
    bool AllowSkip,
    int? Step,
    int? Steps);

/// <summary>How the form ended: the main action, "Decide for me" (the AI fills what's left) or "Skip for now".</summary>
public enum DynamicFormEnd { Submitted, Decided, Skipped }

/// <summary>
/// What was read: the form to draw, or none when it couldn't be (no question could be shown); and what was wrong
/// or left out along the way, to log for the team (never shown to the owner).
/// </summary>
public sealed record DynamicFormRead(DynamicForm? Form, IReadOnlyList<string> Problems);
