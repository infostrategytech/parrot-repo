namespace Parrot.SharedComponents.DesignSystem;

/// <summary>One answer a <see cref="ChoicePrompt"/> offers, with an optional line under it.</summary>
public sealed record ChoicePromptOption(string Label, string? Description = null);

/// <summary>What was picked from a <see cref="ChoicePrompt"/>, in the order shown, and any words of their own.</summary>
public sealed record ChoicePromptAnswer(IReadOnlyList<string> Picked, string? Other);
