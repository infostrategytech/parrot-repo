namespace Parrot.SharedComponents.DesignSystem;

/// <summary>One choice in a <see cref="SegmentedControl{TValue}"/>: its value and the word on it.</summary>
public sealed record SegmentedOption<TValue>(TValue Value, string Label);
