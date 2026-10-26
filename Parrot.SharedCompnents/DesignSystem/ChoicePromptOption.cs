namespace Parrot.SharedComponents.DesignSystem;

/// <summary>One answer a <see cref="ChoicePrompt"/> offers, with an optional line under it.</summary>
public sealed record ChoicePromptOption(string Label, string? Description = null)
{
    /// <summary>The best guess: listed first, its line starting "Recommended · " (at most one per question).</summary>
    public bool Recommended { get; init; }
}

/// <summary>What was picked from a <see cref="ChoicePrompt"/>, in the order shown, and any words of their own.</summary>
public sealed record ChoicePromptAnswer(IReadOnlyList<string> Picked, string? Other);
