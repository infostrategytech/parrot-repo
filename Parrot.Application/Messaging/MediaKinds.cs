using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Messaging;

/// <summary>
/// What kind of thing an attachment is, as the inbox draws it. One vocabulary for every platform, so a
/// WhatsApp sticker and a Messenger sticker are the same kind.
/// </summary>
public static class MediaKinds
{
    public const string Image = "image";
    public const string Gif = "gif";
    public const string Sticker = "sticker";
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Voice = "voice";
    public const string Document = "document";

    /// <summary>A post or page to open, not bytes to keep.</summary>
    public const string Link = "link";

    /// <summary>A GIF is an image the platform did not label; its bytes say so.</summary>
    public static string Refine(string kind, string? contentType) =>
        kind == Image && string.Equals(contentType, "image/gif", StringComparison.OrdinalIgnoreCase) ? Gif : kind;

    /// <summary>What an inbox row says when the message is only a file.</summary>
    public static string PreviewLabel(string kind) => kind switch
    {
        Image => "Photo",
        Gif => "GIF",
        Sticker => "Sticker",
        Video => "Video",
        Voice => "Voice message",
        Audio => "Audio",
        Document => "File",
        Link => "Link",
        _ => "Attachment",
    };

    /// <summary>The media a Messenger or Instagram message carries: pictures, stickers, video, audio and files are downloaded; shares and story mentions are links.</summary>
    internal static List<IncomingMedia>? FromMeta(IEnumerable<FacebookAttachment>? attachments)
    {
        List<IncomingMedia> media = [];
        foreach (FacebookAttachment attachment in attachments ?? [])
        {
            string? url = attachment.Payload?.Url;
            if (string.IsNullOrWhiteSpace(url)) continue;

            string kind = attachment.Type switch
            {
                "image" when attachment.Payload?.StickerId is not null => Sticker,
                "image" => Image,
                "video" => Video,
                "audio" => Audio,
                "file" => Document,
                _ => Link,
            };
            media.Add(new IncomingMedia { Kind = kind, SourceUrl = url });
        }

        return media.Count > 0 ? media : null;
    }
}
