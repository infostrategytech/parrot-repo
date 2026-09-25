namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// Mirrors the Figma component set at node 158:10. The names are the Figma
/// variant names, so a designer saying "secondary, large" and a developer
/// writing <c>Variant="ButtonVariant.Secondary" Size="ButtonSize.Large"</c>
/// mean the same thing.
/// </summary>
public enum ButtonVariant
{
    Primary,
    Secondary,
    Ghost,
    Destructive
}

public enum ButtonSize
{
    Small,
    Medium,
    Large
}

public enum ButtonType
{
    Button,
    Submit,
    Reset
}
