using System.Text.Json;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Domain.Enums;

namespace Parrot.Application.Messaging;

/// <summary>
/// Turns one <see cref="InboundMessage"/> back into the <see cref="IncomingMessage"/>
/// the existing handlers and mappers take, by decoding the provider's raw body.
///
/// One webhook body can hold several messages, and each InboundMessage carries
/// the whole body, so the decoder finds the one this InboundMessage is about by
/// its id. Everything that will never decode throws
/// <see cref="UnprocessableMessageException"/>.
/// </summary>
public interface IInboundMessageDecoder
{
    MessagingPlatform Source { get; }

    IncomingMessage Decode(InboundMessage inbound);
}

/// <summary>Picks the decoder for a message's source.</summary>
public sealed class InboundMessageDecoders(IEnumerable<IInboundMessageDecoder> decoders)
{
    private readonly IReadOnlyDictionary<MessagingPlatform, IInboundMessageDecoder> _bySource =
        decoders.ToDictionary(d => d.Source);

    public IncomingMessage Decode(InboundMessage inbound) =>
        _bySource.TryGetValue(inbound.Source, out IInboundMessageDecoder? decoder)
            ? decoder.Decode(inbound)
            : throw new UnprocessableMessageException($"unknown_source:{inbound.Source}");

    internal static T Parse<T>(InboundMessage inbound)
    {
        if (string.IsNullOrWhiteSpace(inbound.Data)) throw new UnprocessableMessageException("empty_body");
        try
        {
            return JsonSerializer.Deserialize<T>(inbound.Data)
                ?? throw new UnprocessableMessageException("empty_body");
        }
        catch (JsonException ex)
        {
            // Never the body itself in the reason: it holds names and numbers.
            throw new UnprocessableMessageException("body_not_json", ex);
        }
    }

    /// <summary>The provider's own id, from "{platform}:{provider id}".</summary>
    internal static string ProviderId(InboundMessage inbound, string prefix) =>
        inbound.MessageId.StartsWith(prefix + ":", StringComparison.Ordinal) && inbound.MessageId.Length > prefix.Length + 1
            ? inbound.MessageId[(prefix.Length + 1)..]
            : throw new UnprocessableMessageException("message_id_not_prefixed");
}

public sealed class WhatsAppInboundDecoder : IInboundMessageDecoder
{
    public MessagingPlatform Source => MessagingPlatform.WhatsApp;

    public IncomingMessage Decode(InboundMessage inbound)
    {
        string wamid = InboundMessageDecoders.ProviderId(inbound, "whatsapp");
        WhatsAppWebhookPayload payload = InboundMessageDecoders.Parse<WhatsAppWebhookPayload>(inbound);

        foreach (WhatsAppEntry entry in payload.Entry)
        {
            foreach (WhatsAppChange change in entry.Changes)
            {
                if (change.Field != "messages" || change.Value.Messages is null) continue;

                WhatsAppMessage? message = change.Value.Messages.FirstOrDefault(m => m.Id == wamid);
                if (message is null) continue;

                // Same fields as the webhook built before it published raw bodies.
                return new IncomingMessage
                {
                    ConnectionId = inbound.ConnectionId,
                    OwnerUserId = inbound.OwnerUserId,
                    MessageId = inbound.MessageId,
                    Platform = MessagingPlatform.WhatsApp,
                    ExternalAccountId = entry.Id,
                    From = message.From,
                    Timestamp = message.Timestamp,
                    // WhatsApp sends whole seconds.
                    SentAt = ProviderTimestamp.FromUnixSeconds(message.Timestamp) ?? inbound.SentAt,
                    Type = message.Type,
                    // A picture or file's caption is the text the inbox shows with it.
                    TextBody = message.Text?.Body ?? Media(message)?.Caption,
                    Media = Media(message) is { Id.Length: > 0 } media ? [ToMedia(message.Type, media)] : null,
                    ChannelId = change.Value.Metadata.PhoneNumberId,
                    ChannelDisplayName = change.Value.Metadata.DisplayPhoneNumber,
                    ContactName = change.Value.Contacts?.FirstOrDefault(c => c.WaId == message.From)?.Profile?.Name,
                    ReplyToProviderId = message.Context?.Id is { Length: > 0 } quoted ? quoted : null,
                    ReceivedAt = inbound.ReceivedAt,
                };
            }
        }

        throw new UnprocessableMessageException("message_not_in_body");
    }

    private static WhatsAppMedia? Media(WhatsAppMessage message) => message.Type switch
    {
        "image" => message.Image,
        "video" => message.Video,
        "audio" => message.Audio,
        "document" => message.Document,
        "sticker" => message.Sticker,
        _ => null,
    };

    private static IncomingMedia ToMedia(string type, WhatsAppMedia media) => new()
    {
        Kind = MediaKinds.Refine(type switch
        {
            "image" => MediaKinds.Image,
            "video" => MediaKinds.Video,
            "audio" when media.Voice => MediaKinds.Voice,
            "audio" => MediaKinds.Audio,
            "sticker" => MediaKinds.Sticker,
            _ => MediaKinds.Document,
        }, media.MimeType),
        ContentType = media.MimeType,
        FileName = media.Filename,
        ProviderMediaId = media.Id,
    };
}

public sealed class FacebookInboundDecoder : IInboundMessageDecoder
{
    public MessagingPlatform Source => MessagingPlatform.Facebook;

    public IncomingMessage Decode(InboundMessage inbound)
    {
        string mid = InboundMessageDecoders.ProviderId(inbound, "facebook");
        FacebookWebhookPayload payload = InboundMessageDecoders.Parse<FacebookWebhookPayload>(inbound);

        foreach (FacebookEntry entry in payload.Entry)
        {
            foreach (FacebookMessagingEvent evt in entry.Messaging ?? [])
            {
                if (evt.Message is { IsEcho: false } message && message.Mid == mid)
                {
                    List<string>? urls = message.Attachments?
                        .Select(a => a.Payload?.Url)
                        .Where(url => !string.IsNullOrWhiteSpace(url))
                        .Select(url => url!)
                        .ToList();
                    string type = message.Attachments is { Count: > 0 } ? message.Attachments[0].Type : "text";
                    return ToIncoming(inbound, entry, evt, type, message.Text, urls is { Count: > 0 } ? urls : null, MediaKinds.FromMeta(message.Attachments),
                        message.ReplyTo?.Mid is { Length: > 0 } quoted ? quoted : null);
                }

                if (evt.Postback is { } postback && postback.Mid == mid)
                    return ToIncoming(inbound, entry, evt, "postback", postback.Payload, null, null);
            }
        }

        throw new UnprocessableMessageException("message_not_in_body");
    }

    private static IncomingMessage ToIncoming(
        InboundMessage inbound, FacebookEntry entry, FacebookMessagingEvent evt, string type, string? text, IReadOnlyList<string>? urls, IReadOnlyList<IncomingMedia>? media,
        string? replyTo = null) => new()
    {
        ConnectionId = inbound.ConnectionId,
        OwnerUserId = inbound.OwnerUserId,
        MessageId = inbound.MessageId,
        Platform = MessagingPlatform.Facebook,
        ExternalAccountId = entry.Id,
        From = evt.Sender.Id,
        Timestamp = evt.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
        // Messenger sends milliseconds.
        SentAt = ProviderTimestamp.FromUnixMilliseconds(evt.Timestamp) ?? inbound.SentAt,
        Type = type,
        TextBody = text,
        AttachmentUrls = urls,
        Media = media,
        ChannelId = entry.Id,
        // The Page's name comes from the connection, not the webhook body.
        ChannelDisplayName = string.Empty,
        ReplyToProviderId = replyTo,
        ReceivedAt = inbound.ReceivedAt,
    };
}

public sealed class TikTokInboundDecoder : IInboundMessageDecoder
{
    public MessagingPlatform Source => MessagingPlatform.TikTok;

    public IncomingMessage Decode(InboundMessage inbound)
    {
        string messageId = InboundMessageDecoders.ProviderId(inbound, "tiktok");
        if (string.IsNullOrWhiteSpace(inbound.Data)) throw new UnprocessableMessageException("empty_body");
        TikTokWebhookEvent tikTokEvent = TikTokWebhookEvents.Parse(inbound.Data)
            ?? throw new UnprocessableMessageException("body_not_json");

        // The webhook publishes only im_receive_msg, each with the id it is stored under.
        if (tikTokEvent.Name != "im_receive_msg" || tikTokEvent.BusinessOpenId is not { } business
            || tikTokEvent.Message is not { } message || message.MessageId != messageId)
            throw new UnprocessableMessageException("message_not_in_body");

        return new IncomingMessage
        {
            ConnectionId = inbound.ConnectionId,
            OwnerUserId = inbound.OwnerUserId,
            MessageId = inbound.MessageId,
            Platform = MessagingPlatform.TikTok,
            ExternalAccountId = business,
            From = message.SenderId,
            Timestamp = message.Timestamp,
            // TikTok sends milliseconds.
            SentAt = message.SentAt ?? inbound.SentAt,
            Type = message.Type,
            TextBody = message.Text,
            AttachmentUrls = message.ShareUrl is { Length: > 0 } url ? [url] : null,
            // A shared post is a link to open; TikTok's image and sticker payloads are not read yet.
            Media = message.ShareUrl is { Length: > 0 } share ? [new IncomingMedia { Kind = MediaKinds.Link, SourceUrl = share }] : null,
            ChannelId = business,
            // The account's name comes from the connection, not the webhook body.
            ChannelDisplayName = string.Empty,
            ContactName = message.SenderName,
            ExternalConversationId = message.ConversationId,
            ReceivedAt = inbound.ReceivedAt,
        };
    }
}

/// <summary>Instagram DMs have Messenger's shape; entry.id is the business's professional account.</summary>
public sealed class InstagramInboundDecoder : IInboundMessageDecoder
{
    public MessagingPlatform Source => MessagingPlatform.Instagram;

    public IncomingMessage Decode(InboundMessage inbound)
    {
        string mid = InboundMessageDecoders.ProviderId(inbound, "instagram");
        FacebookWebhookPayload payload = InboundMessageDecoders.Parse<FacebookWebhookPayload>(inbound);

        foreach (FacebookEntry entry in payload.Entry)
        {
            foreach (FacebookMessagingEvent evt in entry.Messaging ?? [])
            {
                if (evt.Message is { IsEcho: false } message && message.Mid == mid)
                {
                    List<string>? urls = message.Attachments?
                        .Select(a => a.Payload?.Url)
                        .Where(url => !string.IsNullOrWhiteSpace(url))
                        .Select(url => url!)
                        .ToList();
                    string type = message.Attachments is { Count: > 0 } ? message.Attachments[0].Type : "text";
                    return ToIncoming(inbound, entry, evt, type, message.Text, urls is { Count: > 0 } ? urls : null, MediaKinds.FromMeta(message.Attachments),
                        message.ReplyTo?.Mid is { Length: > 0 } quoted ? quoted : null);
                }

                // Icebreakers and other button taps.
                if (evt.Postback is { } postback && postback.Mid == mid)
                    return ToIncoming(inbound, entry, evt, "postback", postback.Payload, null, null);
            }
        }

        throw new UnprocessableMessageException("message_not_in_body");
    }

    private static IncomingMessage ToIncoming(
        InboundMessage inbound, FacebookEntry entry, FacebookMessagingEvent evt, string type, string? text, IReadOnlyList<string>? urls, IReadOnlyList<IncomingMedia>? media,
        string? replyTo = null) => new()
    {
        ConnectionId = inbound.ConnectionId,
        OwnerUserId = inbound.OwnerUserId,
        MessageId = inbound.MessageId,
        Platform = MessagingPlatform.Instagram,
        ExternalAccountId = entry.Id,
        From = evt.Sender.Id,
        Timestamp = evt.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SentAt = ProviderTimestamp.FromUnixMilliseconds(evt.Timestamp) ?? inbound.SentAt,
        Type = type,
        TextBody = text,
        AttachmentUrls = urls,
        Media = media,
        ChannelId = entry.Id,
        // The username comes from the connection, not the webhook body.
        ChannelDisplayName = string.Empty,
        ReplyToProviderId = replyTo,
        ReceivedAt = inbound.ReceivedAt,
    };
}
