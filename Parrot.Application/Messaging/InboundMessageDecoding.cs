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
                    TextBody = message.Text?.Body,
                    ChannelId = change.Value.Metadata.PhoneNumberId,
                    ChannelDisplayName = change.Value.Metadata.DisplayPhoneNumber,
                    ContactName = change.Value.Contacts?.FirstOrDefault(c => c.WaId == message.From)?.Profile.Name,
                    ReceivedAt = inbound.ReceivedAt,
                };
            }
        }

        throw new UnprocessableMessageException("message_not_in_body");
    }
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
                    return ToIncoming(inbound, entry, evt, type, message.Text, urls is { Count: > 0 } ? urls : null);
                }

                if (evt.Postback is { } postback && postback.Mid == mid)
                    return ToIncoming(inbound, entry, evt, "postback", postback.Payload, null);
            }
        }

        throw new UnprocessableMessageException("message_not_in_body");
    }

    private static IncomingMessage ToIncoming(
        InboundMessage inbound, FacebookEntry entry, FacebookMessagingEvent evt, string type, string? text, IReadOnlyList<string>? urls) => new()
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
        ChannelId = entry.Id,
        // The Page's name comes from the connection, not the webhook body.
        ChannelDisplayName = string.Empty,
        ReceivedAt = inbound.ReceivedAt,
    };
}
