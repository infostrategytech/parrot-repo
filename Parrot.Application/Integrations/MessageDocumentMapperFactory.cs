using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

public sealed class MessageDocumentMapperFactory : IMessageDocumentMapperFactory
{
    private readonly IReadOnlyDictionary<MessagingPlatform, IMessageDocumentMapper> mappers;

    public MessageDocumentMapperFactory(IEnumerable<IMessageDocumentMapper> mappers)
    {
        this.mappers = mappers.ToDictionary(m => m.Platform);

        // A platform without a mapper must fail here, at startup, rather than on the first message of
        // that kind inside the Kafka consumer — where the throw stops ingestion for every platform.
        MessagingPlatform[] missing = Enum.GetValues<MessagingPlatform>()
            .Where(platform => !this.mappers.ContainsKey(platform))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"No IMessageDocumentMapper is registered for: {string.Join(", ", missing)}. "
                + "Register one for every MessagingPlatform value before the host starts.");
        }
    }

    public MessageDocument ToDocument(IncomingMessage message) =>
        mappers.TryGetValue(message.Platform, out IMessageDocumentMapper? mapper)
            ? mapper.ToDocument(message)
            : throw new InvalidOperationException($"No message document mapper registered for platform '{message.Platform}'.");
}

public sealed class WhatsAppMessageDocumentMapper : IMessageDocumentMapper
{
    public MessagingPlatform Platform => MessagingPlatform.WhatsApp;

    // Shared by every mapper: the files the customer sent, with the private copies the worker made.
    internal static List<MessageAttachment>? Attachments(IncomingMessage message) =>
        message.Media is { Count: > 0 } media
            ? [.. media.Select(m => new MessageAttachment
            {
                Kind = m.Kind,
                ContentType = m.ContentType,
                FileName = m.FileName,
                SizeBytes = m.SizeBytes,
                Caption = m.Caption,
                StorageKey = m.StorageKey,
                ProviderMediaId = m.ProviderMediaId,
                SourceUrl = m.SourceUrl,
            })]
            : null;

    // The base fields are set by object initializer rather than a shared helper so that adding a
    // required field to MessageDocument is a compile error in every mapper, not a silent omission.
    public MessageDocument ToDocument(IncomingMessage message) => new WhatsAppMessageDocument
    {
        ConnectionId = message.ConnectionId,
        OwnerUserId = message.OwnerUserId,
        MessageId = message.MessageId,
        Platform = message.Platform,
        ExternalAccountId = message.ExternalAccountId,
        From = message.From,
        Timestamp = message.Timestamp,
        SentAt = message.SentAt,
        Type = message.Type,
        TextBody = message.TextBody,
        ContactName = message.ContactName,
        ReceivedAt = message.ReceivedAt,
        StoredAt = DateTime.UtcNow,
        ChannelId = message.ChannelId,
        ChannelDisplayName = message.ChannelDisplayName,
        Attachments = Attachments(message),
    };
}

// The mapper whose absence previously threw and took the whole consumer down.
public sealed class FacebookMessageDocumentMapper : IMessageDocumentMapper
{
    public MessagingPlatform Platform => MessagingPlatform.Facebook;

    public MessageDocument ToDocument(IncomingMessage message) => new FacebookMessageDocument
    {
        ConnectionId = message.ConnectionId,
        OwnerUserId = message.OwnerUserId,
        MessageId = message.MessageId,
        Platform = message.Platform,
        ExternalAccountId = message.ExternalAccountId,
        From = message.From,
        Timestamp = message.Timestamp,
        SentAt = message.SentAt,
        Type = message.Type,
        TextBody = message.TextBody,
        ContactName = message.ContactName,
        ReceivedAt = message.ReceivedAt,
        StoredAt = DateTime.UtcNow,
        ChannelId = message.ChannelId,
        ChannelDisplayName = message.ChannelDisplayName,
        AttachmentUrls = message.AttachmentUrls is { Count: > 0 } urls ? [.. urls] : null,
        Attachments = WhatsAppMessageDocumentMapper.Attachments(message),
    };
}

public sealed class TikTokMessageDocumentMapper : IMessageDocumentMapper
{
    public MessagingPlatform Platform => MessagingPlatform.TikTok;

    public MessageDocument ToDocument(IncomingMessage message) => new TikTokMessageDocument
    {
        ConversationId = message.ExternalConversationId,
        ConnectionId = message.ConnectionId,
        OwnerUserId = message.OwnerUserId,
        MessageId = message.MessageId,
        Platform = message.Platform,
        ExternalAccountId = message.ExternalAccountId,
        From = message.From,
        Timestamp = message.Timestamp,
        SentAt = message.SentAt,
        Type = message.Type,
        TextBody = message.TextBody,
        ContactName = message.ContactName,
        ReceivedAt = message.ReceivedAt,
        StoredAt = DateTime.UtcNow,
        ChannelId = message.ChannelId,
        ChannelDisplayName = message.ChannelDisplayName,
        AttachmentUrls = message.AttachmentUrls is { Count: > 0 } urls ? [.. urls] : null,
        Attachments = WhatsAppMessageDocumentMapper.Attachments(message),
    };
}

public sealed class InstagramMessageDocumentMapper : IMessageDocumentMapper
{
    public MessagingPlatform Platform => MessagingPlatform.Instagram;

    public MessageDocument ToDocument(IncomingMessage message) => new InstagramMessageDocument
    {
        ConnectionId = message.ConnectionId,
        OwnerUserId = message.OwnerUserId,
        MessageId = message.MessageId,
        Platform = message.Platform,
        ExternalAccountId = message.ExternalAccountId,
        From = message.From,
        Timestamp = message.Timestamp,
        SentAt = message.SentAt,
        Type = message.Type,
        TextBody = message.TextBody,
        ContactName = message.ContactName,
        ReceivedAt = message.ReceivedAt,
        StoredAt = DateTime.UtcNow,
        ChannelId = message.ChannelId,
        ChannelDisplayName = message.ChannelDisplayName,
        AttachmentUrls = message.AttachmentUrls is { Count: > 0 } urls ? [.. urls] : null,
        Attachments = WhatsAppMessageDocumentMapper.Attachments(message),
    };
}
