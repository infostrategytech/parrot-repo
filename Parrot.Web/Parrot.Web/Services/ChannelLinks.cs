namespace Parrot.Web.Services;

/// <summary>What a <see cref="ChannelLink"/> opens on the customer's platform.</summary>
public enum ChannelLinkKind
{
    /// <summary>The customer's chat itself.</summary>
    Chat,
    /// <summary>The customer's profile page, where the business taps Message.</summary>
    Profile,
    /// <summary>The platform's inbox, where the business finds the customer: Meta and TikTok publish no link to one thread.</summary>
    Inbox,
}

/// <summary>Where the business can continue a conversation on the customer's own platform.</summary>
/// <param name="Url">The address to open in a new tab.</param>
/// <param name="Platform">The platform's name, as people know it (Messenger, not Facebook).</param>
/// <param name="Kind">What the address opens.</param>
public sealed record ChannelLink(string Url, string Platform, ChannelLinkKind Kind)
{
    public bool OpensChat => Kind == ChannelLinkKind.Chat;

    public string ButtonLabel => Kind switch
    {
        ChannelLinkKind.Chat => $"Open chat in {Platform}",
        ChannelLinkKind.Profile => $"Open profile on {Platform}",
        _ => $"Open {Platform} inbox",
    };

    public string Hint(string customerName) => Kind switch
    {
        ChannelLinkKind.Chat => $"Message {customerName} directly",
        ChannelLinkKind.Profile => $"Opens {customerName}'s {Platform} profile. Tap Message there.",
        _ => $"Opens your {Platform} inbox. Find {customerName} there.",
    };
}

public static class ChannelLinks
{
    public static string PlatformName(string channel) => channel switch
    {
        "facebook" => "Messenger",
        "instagram" => "Instagram",
        "tiktok" => "TikTok",
        _ => "WhatsApp",
    };

    /// <summary>
    /// The link for a customer's conversation, or null when none can be built. WhatsApp's sender is the customer's
    /// phone number, so it opens the chat itself. Facebook and Instagram open the customer's profile page: Facebook
    /// from the sender id, Instagram from the @handle Instagram gave with the first message (the sender id is not an address).
    /// Without a handle, and for TikTok, the link opens the business's inbox on that platform.
    /// </summary>
    public static ChannelLink? For(string channel, string sender, string? accountId, string? username = null)
    {
        switch (channel)
        {
            case "whatsapp":
                string digits = new([.. sender.Where(char.IsAsciiDigit)]);
                // wa.me takes the number in international form, digits only: no +, spaces or leading zeros.
                digits = digits.TrimStart('0');
                return digits.Length >= 7 ? new ChannelLink($"https://wa.me/{digits}", "WhatsApp", ChannelLinkKind.Chat) : null;
            case "facebook":
                if (sender.Length > 0 && sender.All(char.IsAsciiDigit))
                    return new ChannelLink($"https://www.facebook.com/{sender}", "Messenger", ChannelLinkKind.Profile);
                string page = accountId is { Length: > 0 } && accountId.All(char.IsAsciiDigit) ? $"?asset_id={accountId}" : "";
                return new ChannelLink($"https://business.facebook.com/latest/inbox/messenger{page}", "Messenger", ChannelLinkKind.Inbox);
            case "instagram":
                string? handle = username?.TrimStart('@');
                return handle is { Length: > 0 } && handle.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_')
                    ? new ChannelLink($"https://www.instagram.com/{handle}/", "Instagram", ChannelLinkKind.Profile)
                    : new ChannelLink("https://www.instagram.com/direct/inbox/", "Instagram", ChannelLinkKind.Inbox);
            case "tiktok":
                return new ChannelLink("https://www.tiktok.com/messages", "TikTok", ChannelLinkKind.Inbox);
            default:
                return null;
        }
    }
}
