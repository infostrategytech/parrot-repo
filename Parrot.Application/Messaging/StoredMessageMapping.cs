using System.Globalization;
using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Messaging;

/// <summary>
/// A stored document as the CRM sees it. The realtime event and the history
/// lookup both build <see cref="StoredMessage"/> from here, so a pushed
/// message and the same message read from history are identical, except that
/// only the event is given a link signer to put download links in.
/// </summary>
public static class StoredMessageMapping
{
    /// <param name="document">The stored message.</param>
    /// <param name="links">Makes the week-long download links the event carries; null leaves every url null.</param>
    /// <exception cref="InvalidOperationException">The document has no conversation key or sequence: it predates the worker, or is a reply to a message that does.</exception>
    public static StoredMessage ToStoredMessage(this MessageDocument document, IMediaLinkSigner? links = null) => new()
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
            ? [.. attachments.Select((a, index) => ToStoredAttachment(
                a, document.SentAt ?? document.StoredAt, attachments.Count > 1 ? index + 1 : null, links))]
            : null,
        SentAt = document.SentAt,
        PersistedAt = document.StoredAt,
        ReplyTo = ToStoredQuote(document.ReplyTo, links),
    };

    private static StoredAttachment ToStoredAttachment(MessageAttachment a, DateTime sentAt, int? number, IMediaLinkSigner? links)
    {
        // A shared post is not a file: its address is what the inbox opens, and it has no name to give.
        if (a.Kind == MediaKinds.Link)
            return new StoredAttachment(a.Kind, a.SourceUrl, a.ContentType, a.FileName, a.SizeBytes, true, MediaStatus.Of(a));

        string fileName = NameFile(a, sentAt, number);
        string? url = a.StorageKey is { } key ? links?.Sign(key, fileName) : null;
        return new StoredAttachment(a.Kind, url, a.ContentType, fileName, a.SizeBytes, a.StorageKey is not null, MediaStatus.Of(a));
    }

    // WhatsApp names only a document. A photo, video or voice note is named from its kind and when it was sent,
    // with the saved copy's extension: image-20261123-132153.jpg. A number tells apart files sent together.
    private static string NameFile(MessageAttachment a, DateTime sentAt, int? number) =>
        !string.IsNullOrWhiteSpace(a.FileName)
            ? a.FileName
            : $"{a.Kind}-{sentAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}{(number is { } n ? $"-{n}" : "")}{Path.GetExtension(a.StorageKey ?? "")}";

    // The quoted file's link comes from the copy the reply kept, so a reader can see what "this one" is.
    private static StoredQuote? ToStoredQuote(MessageReplyTo? reply, IMediaLinkSigner? links) =>
        reply is null
            ? null
            : new StoredQuote(reply.MessageId, reply.Outgoing, reply.Text, reply.Type,
                reply.StorageKey is { } key ? links?.Sign(key, $"{reply.Type ?? "file"}{Path.GetExtension(key)}") : null);
}
