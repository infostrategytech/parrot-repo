namespace Parrot.SharedComponents.DesignSystem;

/// <summary>One image in a <see cref="MediaViewer"/>: the file, and who sent it when.</summary>
public sealed record MediaViewerItem(string Url, string Name, string Sender, string SenderSeed, string SenderInitials, string SentAt);
