namespace Parrot.SharedComponents.DesignSystem;

/// <summary>Presentation only. The host owns delivery, grouping, read state and source navigation.</summary>
public sealed record NotificationListItem(string Id, string Title, string Meta, IconName Glyph,
    NotificationListTone Tone = NotificationListTone.Neutral, string? Detail = null,
    string? ActionLabel = null, bool Unread = false, bool Resolved = false, string? Section = null);

public enum NotificationListTone { Neutral, Pink, Blue, Amber, Emerald, Violet, Brand, Success }

/// <summary>Feed places the action at the trailing edge; Compact keeps it under the copy in a narrow panel.</summary>
public enum NotificationListLayout { Compact, Feed }
