namespace Parrot.Domain.Entities.Messages;

/// <summary>
/// One file, picture, sticker, GIF, voice note or video a customer sent with a message. Meta's media links
/// expire (WhatsApp's within minutes, and only with the connection's token), so the worker copies the bytes
/// to private storage when the message arrives; <see cref="StorageKey"/> names that copy.
/// </summary>
public sealed class MessageAttachment
{
    /// <summary>image, gif, sticker, video, audio, voice, document or link: what the inbox draws.</summary>
    public required string Kind { get; set; }

    public string? ContentType { get; set; }

    public string? FileName { get; set; }

    public long? SizeBytes { get; set; }

    /// <summary>The text the customer wrote with the file, when the platform keeps it apart from the message text.</summary>
    public string? Caption { get; set; }

    /// <summary>Where the private copy lives. Null when it could not be saved (too large, expired, refused).</summary>
    public string? StorageKey { get; set; }

    /// <summary>See <see cref="MediaStatus"/>. Null on messages stored before it existed; read it with <see cref="MediaStatus.Of"/>.</summary>
    public string? Status { get; set; }

    /// <summary>A short reason code when <see cref="Status"/> is failed (refused, empty, expired, error). Never the platform's link.</summary>
    public string? FailureReason { get; set; }

    /// <summary>The platform's id for the media (WhatsApp's), kept so the file can be fetched again.</summary>
    public string? ProviderMediaId { get; set; }

    /// <summary>A link the platform gave: a post to open (TikTok) or the media's address at the time. Never served to the inbox as media.</summary>
    public string? SourceUrl { get; set; }
}
