using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Messaging;

/// <summary>
/// A stored document as the CRM sees it. The realtime event and the history
/// lookup both build <see cref="StoredMessage"/> from here, so a pushed
/// message and the same message read from history are identical.
/// </summary>
public static class StoredMessageMapping
{
    /// <exception cref="InvalidOperationException">The document has no conversation key or sequence: it predates the worker, or is a reply to a message that does.</exception>
    public static StoredMessage ToStoredMessage(this MessageDocument document) => new()
    {
        MessageId = document.MessageId,
        ConversationKey = document.ConversationKey
            ?? throw new InvalidOperationException("The stored message has no conversation key."),
        Sequence = document.Sequence
            ?? throw new InvalidOperationException("The stored message has no sequence."),
        Platform = document.Platform,
        ConnectionId = document.ConnectionId,
        OwnerUserId = document.OwnerUserId,
        WorkspaceId = document.OwnerUserId,
        TenantId = document.OwnerUserId,
        Outgoing = document.Outgoing,

        // From is always the customer, for both directions (the inbox groups by it);
        // Receiver is always the business. On a reply the two swap roles.
        Sender = document.Outgoing
            ? document.Receiver ?? string.Empty
            : ConversationKey.NormalizeParticipant(document.Platform, document.From),
        Receiver = document.Outgoing
            ? ConversationKey.NormalizeParticipant(document.Platform, document.From)
            : document.Receiver ?? string.Empty,
        SenderName = document.ContactName,
        Type = document.Type,
        Text = document.TextBody,
        AttachmentUrls = document switch
        {
            FacebookMessageDocument facebook => facebook.AttachmentUrls,
            TikTokMessageDocument tikTok => tikTok.AttachmentUrls,
            InstagramMessageDocument instagram => instagram.AttachmentUrls,
            _ => null,
        },
        Attachments = document.Attachments is { Count: > 0 } attachments
            ? [.. attachments.Select(a => new StoredAttachment(
                a.Kind, a.ContentType, a.FileName, a.SizeBytes, a.StorageKey is not null || a.Kind == MediaKinds.Link, MediaStatus.Of(a)))]
            : null,
        SentAt = document.SentAt,
        PersistedAt = document.StoredAt,
        ReplyTo = Parrot.Application.Conversations.ConversationService.ToQuote(document.ReplyTo),
    };
}
