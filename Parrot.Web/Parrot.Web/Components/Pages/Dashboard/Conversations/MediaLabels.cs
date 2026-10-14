using System.Globalization;
using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

/// <summary>A picture the agent chose in a thread, to open full screen.</summary>
public sealed record MediaPreview(ConvMessage Message, InboxMediaDto Media);

/// <summary>How a customer's file is worded: its name, size and the glyph that stands for it.</summary>
public static class MediaLabels
{
    public static string Name(InboxMediaDto file) => !string.IsNullOrWhiteSpace(file.FileName) ? file.FileName! : file.Kind switch
    {
        "image" => "Photo",
        "gif" => "GIF",
        "sticker" => "Sticker",
        "video" => "Video",
        "voice" => "Voice message",
        "audio" => "Audio",
        "link" => "Shared post",
        _ => "File",
    };

    /// <summary>"2.4 MB", or empty when the platform sent no size.</summary>
    public static string Size(long? bytes) => bytes switch
    {
        null or <= 0 => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB".Replace(",", "."),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MB"),
    };

    /// <summary>The file's type as a word for the details line: "PDF", "MP3"... from its name or type.</summary>
    public static string Type(InboxMediaDto file)
    {
        var extension = Path.GetExtension(file.FileName ?? "").TrimStart('.');
        if (extension.Length is > 0 and <= 5) return extension.ToUpperInvariant();
        var subtype = file.ContentType?.Split('/').LastOrDefault()?.Split(';')[0];
        return string.IsNullOrWhiteSpace(subtype) ? "" : subtype.ToUpperInvariant();
    }

    public static IconName Glyph(InboxMediaDto file) => file.Kind switch
    {
        "image" or "gif" or "sticker" => IconName.RiImageFill,
        "video" => IconName.RiPlayFill,
        "audio" or "voice" => IconName.RiMicFill,
        "link" => IconName.RiLinksFill,
        _ => (file.ContentType, Path.GetExtension(file.FileName ?? "").ToLowerInvariant()) switch
        {
            ("application/pdf", _) or (_, ".pdf") => IconName.RiFilePdf2Fill,
            (_, ".doc" or ".docx") => IconName.RiFileWord2Fill,
            (_, ".xls" or ".xlsx" or ".csv") => IconName.RiFileExcel2Fill,
            _ => IconName.RiFileTextFill,
        },
    };
}
