namespace Parrot.SharedComponents.DesignSystem;

/// <summary>How a <see cref="FormQuestion"/> is answered: one choice, several, or a few words.</summary>
public enum FormQuestionKind { One, Many, Text }

/// <summary>
/// One question in a <see cref="QuestionForm"/>. Choices are
/// <see cref="ChoicePromptOption"/>s, as in a <see cref="ChoicePrompt"/>;
/// <paramref name="AllowOther"/> adds a row to answer in their own words.
/// </summary>
public sealed record FormQuestion(
    string Id,
    string Question,
    FormQuestionKind Kind,
    IReadOnlyList<ChoicePromptOption>? Options = null,
    string? Placeholder = null,
    bool AllowOther = false);

/// <summary>An answer to a <see cref="FormQuestion"/>: what was picked, in the order shown, and any words of their own.</summary>
public sealed record FormAnswer(IReadOnlyList<string> Picked, string? Text = null)
{
    public static readonly FormAnswer None = new([], null);

    public bool IsEmpty => Picked.Count == 0 && string.IsNullOrWhiteSpace(Text);
}
