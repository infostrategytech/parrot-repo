namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// What a badge means, never what colour it is. A caller that asks for
/// "green" has to be revisited when the palette changes; one that asks for
/// <see cref="Success"/> does not.
/// </summary>
public enum StatusTone
{
    Neutral,
    Brand,
    Success,
    Warning,
    Danger,
    Info
}
