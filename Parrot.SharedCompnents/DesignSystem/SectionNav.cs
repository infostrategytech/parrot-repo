namespace Parrot.SharedComponents.DesignSystem;

/// <summary>A group of sections under a quiet title ("You", "Business"), with an optional line under it
/// ("Owners and admins").</summary>
public sealed record SectionNavGroup(string? Title, IReadOnlyList<SectionNavItem> Items, string? Note = null);

/// <summary>One section: its id, its name, the address that shows it, and an icon to scan by.</summary>
public sealed record SectionNavItem(string Id, string Label, string Href, IconName? Glyph = null);
