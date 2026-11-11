namespace Parrot.SharedComponents.DesignSystem;

/// <summary>One pop-up: the bell row's words and mark, and an optional action. Presentation only.</summary>
public sealed record NotificationPopupItem(string Id, string Title, string Meta, IconName Glyph,
    NotificationListTone Tone = NotificationListTone.Neutral, string? Detail = null, string? ActionLabel = null);
