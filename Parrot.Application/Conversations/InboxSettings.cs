namespace Parrot.Application.Conversations;

public sealed class InboxSettings
{
    public const string SectionName = "Inbox";

    /// <summary>
    /// Optional. Where unread counting starts: messages stored before it count as read, so turning the feature
    /// on does not show a user their whole history as unread. When not set, the time the API first started with
    /// this feature is saved in MongoDB and used from then on. It must never move later: that would mark
    /// messages that really are unread as read.
    /// </summary>
    public DateTime? UnreadBaseline { get; set; }

    /// <summary>
    /// When an agent reads a conversation, tell the customer's channel too (WhatsApp's blue ticks, Messenger
    /// and Instagram's "Seen"). On by default; set false to keep reads inside Parrot.
    /// </summary>
    public bool SendReadReceipts { get; set; } = true;
}
