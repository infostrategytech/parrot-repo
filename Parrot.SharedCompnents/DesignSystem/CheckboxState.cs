namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// A checkbox has three states, not two. <see cref="Indeterminate"/> means
/// "some of the things below are selected" and is what a parent row shows over
/// a partially selected group.
/// </summary>
public enum CheckboxState
{
    Unchecked,
    Checked,
    Indeterminate
}
