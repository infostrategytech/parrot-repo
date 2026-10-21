namespace Parrot.SharedComponents.Layout;

/// <summary>One workspace the switcher can offer: a stable id, its name and, optionally, its logo.</summary>
public sealed record WorkspaceOption(string Id, string Name, string? LogoUrl);
