namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// <see cref="Error"/> means something failed. <see cref="Warning"/> means it
/// worked but needs attention. <see cref="Info"/> is neither — use it sparingly,
/// because a box that looks like an error and is not teaches people to ignore
/// the ones that are.
/// </summary>
public enum InlineErrorTone
{
    Error,
    Warning,
    Info
}
