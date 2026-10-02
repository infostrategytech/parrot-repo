using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Messaging;

/// <summary>
/// A stored document as the CRM sees it. The realtime event and the history
/// lookup both build <see cref="StoredMessage"/> from here, so a pushed
/// message and the same message read from history are identical.
/// </summary>
public static class StoredMessageMapping
{
    /// <exception cref="InvalidOperationException">The document has no conversation key or sequence: it predates the worker.</exception>
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
        Outgoing = document.Outgoing,
        Sender = ConversationKey.NormalizeParticipant(document.Platform, document.From),
        Receiver = document.Receiver ?? string.Empty,
        SenderName = document.ContactName,
        Type = document.Type,
        Text = document.TextBody,
        AttachmentUrls = (document as FacebookMessageDocument)?.AttachmentUrls,
        SentAt = document.SentAt,
        PersistedAt = document.StoredAt,
    };
}
