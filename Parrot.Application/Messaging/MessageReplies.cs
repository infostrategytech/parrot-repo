using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

namespace Parrot.Application.Messaging;

/// <summary>
/// Replies that quote a message, in either direction. A stored message is known to its channel by a
/// different id than the inbox's: a customer's message by the id after "{platform}:" in its MessageId,
/// our reply by the ProviderMessageId the channel answered the send with.
/// </summary>
public static class MessageReplies
{
    public static string Prefix(MessagingPlatform platform) => platform.ToString().ToLowerInvariant();

    /// <summary>The channel's id for a stored message, or null when it has none (a reply that never reached the channel).</summary>
    public static string? ProviderIdOf(MessageDocument message)
    {
        if (message.Outgoing) return string.IsNullOrEmpty(message.ProviderMessageId) ? null : message.ProviderMessageId;
        string prefix = Prefix(message.Platform) + ":";
        return message.MessageId.StartsWith(prefix, StringComparison.Ordinal) && message.MessageId.Length > prefix.Length
            ? message.MessageId[prefix.Length..]
            : null;
    }

    /// <summary>The copy of <paramref name="quoted"/> a reply keeps, so its quote shows without reading the original.</summary>
    public static MessageReplyTo Quote(MessageDocument quoted, string providerMessageId)
    {
        MessageAttachment? file = quoted.Attachments?.FirstOrDefault();
        return new MessageReplyTo
        {
            ProviderMessageId = providerMessageId,
            MessageId = quoted.MessageId,
            Outgoing = quoted.Outgoing,
            Text = string.IsNullOrWhiteSpace(quoted.TextBody) ? file?.Caption : quoted.TextBody,
            Type = file?.Kind ?? quoted.Type,
        };
    }
}
