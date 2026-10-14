namespace Parrot.Web.Services;

/// <summary>Where the business can continue a conversation on the customer's own platform.</summary>
/// <param name="Url">The address to open in a new tab.</param>
/// <param name="Platform">The platform's name, as people know it (Messenger, not Facebook).</param>
/// <param name="OpensChat">
/// True when the link opens this customer's chat. False when it opens the platform's inbox and the business
/// finds the customer there: Meta and TikTok publish no link to one customer's thread.
/// </param>
public sealed record ChannelLink(string Url, string Platform, bool OpensChat)
{
    public string ButtonLabel => OpensChat ? $"Open chat in {Platform}" : $"Open {Platform} inbox";

    public string Hint(string customerName) => OpensChat
        ? $"Message {customerName} directly"
        : $"Opens your {Platform} inbox. Find {customerName} there.";
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
    /// The link for a customer's conversation, or null when none can be built. WhatsApp is the one channel whose
    /// sender is something a link can use (the customer's phone number), so it opens the chat itself. A Messenger,
    /// Instagram or TikTok sender is an opaque id with no profile address, so those open the business's inbox on
    /// that platform.
    /// </summary>
    public static ChannelLink? For(string channel, string sender, string? accountId)
    {
        switch (channel)
        {
            case "whatsapp":
                string digits = new([.. sender.Where(char.IsAsciiDigit)]);
                // wa.me takes the number in international form, digits only: no +, spaces or leading zeros.
                digits = digits.TrimStart('0');
                return digits.Length >= 7 ? new ChannelLink($"https://wa.me/{digits}", "WhatsApp", true) : null;
            case "facebook":
                string page = accountId is { Length: > 0 } && accountId.All(char.IsAsciiDigit) ? $"?asset_id={accountId}" : "";
                return new ChannelLink($"https://business.facebook.com/latest/inbox/messenger{page}", "Messenger", false);
            case "instagram":
                return new ChannelLink("https://www.instagram.com/direct/inbox/", "Instagram", false);
            case "tiktok":
                return new ChannelLink("https://www.tiktok.com/messages", "TikTok", false);
            default:
                return null;
        }
    }
}
