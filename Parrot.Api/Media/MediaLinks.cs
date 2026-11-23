using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Parrot.Application.Conversations;

namespace Parrot.Api.Media;

/// <summary>
/// Signed, expiring addresses for customers' files. A browser's &lt;img&gt; and &lt;video&gt; cannot send the
/// sign-in token, so the API hands the inbox an address that carries its own proof: the storage key and file
/// name, sealed with the API's key ring and valid for <see cref="Lifetime"/>. The inbox only receives one for a
/// message the signed-in user owns, because the address is minted while reading that user's conversation.
/// </summary>
public sealed class MediaLinks(IDataProtectionProvider provider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly ITimeLimitedDataProtector _protector =
        provider.CreateProtector(Parrot.Infrastructure.Storage.MediaLinkSigner.Purpose).ToTimeLimitedDataProtector();

    /// <summary>A copy of the media with Url set to its signed address, or unchanged when no copy was saved.</summary>
    public MessageMedia Sign(MessageMedia media) =>
        media.StorageKey is { } key
            ? media with { Url = "api/media/" + _protector.Protect($"{key}\n{media.FileName}", Lifetime) }
            : media;

    public ConversationMessage Sign(ConversationMessage message) =>
        message.Attachments is { Count: > 0 } attachments ? message with { Attachments = [.. attachments.Select(Sign)] } : message;

    public MessagePage Sign(MessagePage page) => page with { Messages = [.. page.Messages.Select(Sign)] };

    public MediaPage Sign(MediaPage page) =>
        page with { Items = [.. page.Items.Select(item => item with { Media = Sign(item.Media) })] };

    /// <summary>The storage key and file name inside a token; false when it is forged, malformed or expired.</summary>
    public bool TryRead(string token, out string key, out string? fileName)
    {
        key = "";
        fileName = null;
        try
        {
            string[] parts = _protector.Unprotect(token).Split('\n', 2);
            if (parts[0].Length == 0) return false;
            key = parts[0];
            fileName = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
