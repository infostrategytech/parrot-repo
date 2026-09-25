namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// Only <see cref="Destructive"/> changes the dialog's appearance. Figma draws
/// Informational the same as Default today; the case exists so a caller can
/// say what a dialog is for without waiting for that to be resolved.
/// </summary>
public enum DialogType
{
    Default,
    Destructive,
    Informational
}
