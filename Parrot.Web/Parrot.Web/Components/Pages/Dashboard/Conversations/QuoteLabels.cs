using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

/// <summary>How a quoted message is shown, above a reply in the thread and in the composer: who wrote it and what it said.</summary>
public static class QuoteLabels
{
    /// <summary>The quote a reply to <paramref name="message"/> carries, as the thread shows it before the API answers.</summary>
    public static InboxQuoteDto From(ConvMessage message) => new(message.Id, message.Kind is MessageKind.Ai or MessageKind.Human,
        message.Text.Length > 0 ? message.Text : null, message.Attachments.FirstOrDefault()?.Kind ?? message.Attachment?.Type);

    /// <summary>"You" for the business's own messages, as WhatsApp shows it; the customer's name for theirs.</summary>
    public static string Author(InboxQuoteDto quote, string customerName) => quote.Outgoing ? "You" : customerName;

    /// <summary>The glyph for a quote with no words (a photo, a voice message…); null when it has words.</summary>
    public static Parrot.SharedComponents.DesignSystem.IconName? Glyph(InboxQuoteDto quote) => !string.IsNullOrWhiteSpace(quote.Text) ? null : quote.Type switch
    {
        "image" or "gif" or "sticker" => Parrot.SharedComponents.DesignSystem.IconName.RiImageFill,
        "video" => Parrot.SharedComponents.DesignSystem.IconName.RiPlayFill,
        "voice" or "audio" => Parrot.SharedComponents.DesignSystem.IconName.RiMicFill,
        "document" or "file" => Parrot.SharedComponents.DesignSystem.IconName.RiFileTextFill,
        "link" => Parrot.SharedComponents.DesignSystem.IconName.RiLinksFill,
        _ => null,
    };

    /// <summary>The quoted text, or what kind of message it was when it had none.</summary>
    public static string Text(InboxQuoteDto quote) => !string.IsNullOrWhiteSpace(quote.Text) ? quote.Text! : quote.Type switch
    {
        "image" => "Photo",
        "gif" => "GIF",
        "sticker" => "Sticker",
        "video" => "Video",
        "voice" => "Voice message",
        "audio" => "Audio",
        "document" or "file" => "Document",
        "link" => "Shared post",
        _ => "Message",
    };

    /// <summary>Whether a message can be replied to: a customer's or a reply that reached the channel (one it has an id for).</summary>
    public static bool CanQuote(ConvMessage message) => message.Kind switch
    {
        MessageKind.Customer => true,
        MessageKind.Ai or MessageKind.Human => message.Status is "accepted" or "sent" or "delivered" or "read",
        _ => false,
    };
}
