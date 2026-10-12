using System.Globalization;
using System.Text.Json;

namespace Parrot.Application.Integrations;

/// <summary>
/// What Parrot reads from a TikTok Business Messaging webhook:
/// <c>{"event": "im_receive_msg", "user_openid": "&lt;business&gt;", "content": "{...}"}</c>, where
/// <c>content</c> is itself JSON, as text. The webhook and the Message Worker both read it through
/// <see cref="TikTokWebhookEvents.Parse"/>, so they agree on what a body holds.
/// </summary>
/// <param name="Name">"im_receive_msg" for a customer's message; "im_send_msg" for the business's own (an echo).</param>
/// <param name="BusinessOpenId">The Business Account the event is about.</param>
/// <param name="Message">The message in <c>content</c>, when it holds one with an id and a sender.</param>
public sealed record TikTokWebhookEvent(string Name, string? BusinessOpenId, TikTokMessage? Message);

public sealed record TikTokMessage(
    string MessageId,
    string? ConversationId,
    string SenderId,
    string? SenderName,
    // "text", "image", "sticker", "share_post"; anything else TikTok adds is kept as sent, lower-cased.
    string Type,
    string? Text,
    string? ShareUrl,
    // As TikTok sent it (milliseconds), kept for provenance; empty when it sent none.
    string Timestamp,
    DateTime? SentAt);

public static class TikTokWebhookEvents
{
    /// <summary>Null when the body is not a TikTok event at all.</summary>
    public static TikTokWebhookEvent? Parse(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return null;

        try
        {
            using JsonDocument envelope = JsonDocument.Parse(rawBody);
            if (envelope.RootElement.ValueKind != JsonValueKind.Object) return null;

            string name = Text(envelope.RootElement, "event") ?? string.Empty;
            string? business = Text(envelope.RootElement, "user_openid");
            if (name.Length == 0) return null;

            return new TikTokWebhookEvent(name, business, ParseContent(envelope.RootElement));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // "content" arrives as a JSON string; an object is accepted too.
    private static TikTokMessage? ParseContent(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("content", out JsonElement content)) return null;

        if (content.ValueKind == JsonValueKind.Object) return ToMessage(content);
        if (content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString())) return null;

        try
        {
            using JsonDocument inner = JsonDocument.Parse(content.GetString()!);
            return inner.RootElement.ValueKind == JsonValueKind.Object ? ToMessage(inner.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TikTokMessage? ToMessage(JsonElement content)
    {
        string? messageId = Text(content, "message_id");
        string? senderId = content.TryGetProperty("from_user", out JsonElement from) && from.ValueKind == JsonValueKind.Object
            ? Text(from, "id")
            : null;
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(senderId)) return null;

        string timestamp = Text(content, "timestamp") ?? string.Empty;
        DateTime? sentAt = long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds)
            ? ProviderTimestamp.FromUnixMilliseconds(milliseconds)
            : null;

        return new TikTokMessage(
            messageId,
            Text(content, "conversation_id"),
            senderId,
            Text(content, "from"),
            Text(content, "type")?.ToLowerInvariant() ?? "unsupported",
            content.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.Object ? Text(text, "body") : null,
            content.TryGetProperty("share_post", out JsonElement share) && share.ValueKind == JsonValueKind.Object ? Text(share, "embed_url") : null,
            timestamp,
            sentAt);
    }

    // Ids and times are strings or numbers depending on the field; both read as text.
    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}
