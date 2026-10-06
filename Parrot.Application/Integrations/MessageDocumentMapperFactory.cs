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
    };
}
