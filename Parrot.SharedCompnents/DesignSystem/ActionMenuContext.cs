namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// Shared by an ActionMenu and its items so the rows can line up.
///
/// If any row has an icon, every row reserves the space for one — otherwise a
/// menu that mixes "Edit item" (with a pencil) and "All details" (without)
/// starts its labels at two different places, which reads as a mistake. A menu
/// with no icons at all keeps its labels against the edge, with nothing
/// reserved.
/// </summary>
public sealed class ActionMenuContext
{
    /// <summary>True once any item in this menu has declared an icon.</summary>
    public bool ReservesIconColumn { get; private set; }

    /// <summary>Raised when the answer changes, so the menu can draw again.</summary>
    public event Action? Changed;

    public void DeclareIcon()
    {
        if (ReservesIconColumn) return;
        ReservesIconColumn = true;
        Changed?.Invoke();
    }
}
