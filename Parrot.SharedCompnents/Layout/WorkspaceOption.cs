namespace Parrot.SharedComponents.Layout;

/// <summary>
/// One workspace the switcher can offer: a stable id, its name and, optionally, its logo.
/// <paramref name="Tag"/> is the person's role there, in another business's workspace (their own carry none);
/// <paramref name="Group"/> is the business it belongs to, null for the person's own;
/// <paramref name="IsDefault"/> marks the one sign-in always opens.
/// </summary>
public sealed record WorkspaceOption(string Id, string Name, string? LogoUrl, string? Tag = null, string? Group = null, bool IsDefault = false);
