namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// Opt-in number entry. None leaves the existing text/authentication behaviour unchanged. Phone takes digits
/// and what people write a number with (+, spaces, brackets, dashes), never letters (designer, 28 October 2026).
/// </summary>
public enum NumericInputKind { None, Integer, Decimal, Phone }
