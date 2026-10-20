namespace Parrot.SharedComponents.Layout;

/// <summary>One workspace the signed-in person can switch to.</summary>
public sealed record WorkspaceMenuItem(
    string Id,
    string Name,
    string? Plan = null,
    string? LogoUrl = null);
