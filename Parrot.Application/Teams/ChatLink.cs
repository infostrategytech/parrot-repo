using System.Text.RegularExpressions;
using Parrot.Domain.Enums;

namespace Parrot.Application.Teams;

/// <summary>
/// A link that opens a chat with the business on its own channel, from a phone or a browser: wa.me for a WhatsApp
/// number, ig.me for an Instagram account, m.me for a Facebook page. TikTok has no such link, so it has none.
/// </summary>
public static partial class ChatLink
{
    /// <summary>The link for the channel's handle (the phone number, username or page id), or null when there isn't one.</summary>
    public static string? For(MessagingPlatform platform, string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return null;
        switch (platform)
        {
            case MessagingPlatform.WhatsApp:
                // wa.me takes the number with its country code, digits only: "+234 803 123 4567" is 2348031234567.
                string digits = NotDigit().Replace(handle, "");
                return digits.Length is >= 7 and <= 15 ? $"https://wa.me/{digits}" : null;
            case MessagingPlatform.Instagram:
                string username = handle.Trim().TrimStart('@');
                return InstagramName().IsMatch(username) ? $"https://ig.me/m/{username}" : null;
            case MessagingPlatform.Facebook:
                string page = handle.Trim();
                return page.All(char.IsAsciiDigit) ? $"https://m.me/{page}" : null;
            default:
                return null;
        }
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex NotDigit();

    [GeneratedRegex(@"^[A-Za-z0-9._]{1,30}$")]
    private static partial Regex InstagramName();
}
