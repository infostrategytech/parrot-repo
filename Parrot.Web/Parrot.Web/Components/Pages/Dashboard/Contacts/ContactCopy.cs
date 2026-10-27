using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Components.Pages.Dashboard.Contacts;

/// <summary>
/// The words and tones for a contact's stage and a task request's status, in
/// one place so the list, the panel and the form agree. Mirrors Aurora's
/// ProductCopy.
/// </summary>
public static class ContactCopy
{
    public static string Stage(string stage) => stage switch
    {
        "Vip" => "VIP",
        _ => stage
    };

    public static StatusTone StageTone(string stage) => stage switch
    {
        "New Lead" => StatusTone.Info,
        "Interested" => StatusTone.Warning,
        "Customer" => StatusTone.Success,
        "Vip" => StatusTone.Brand,
        _ => StatusTone.Neutral
    };

    public static StatusTone OrderTone(string status) => status switch
    {
        "Completed" => StatusTone.Success,
        "Pending" => StatusTone.Warning,
        "Cancelled" => StatusTone.Danger,
        _ => StatusTone.Neutral
    };

    public static IconName OrderGlyph(string status) => status switch
    {
        "Completed" => IconName.RiCheckFill,
        "Pending" => IconName.RiTimeFill,
        "Cancelled" => IconName.RiCloseFill,
        _ => IconName.RiInformationFill
    };

    /// <summary>
    /// How they are reached on a channel, under their name: "@damiade", "+2348064192084". Null for an id that is
    /// not an address (Messenger's, or TikTok's without a handle).
    /// </summary>
    public static string? Handle(ContactIdentity identity) => identity switch
    {
        { Username: { Length: > 0 } username } => "@" + username.TrimStart('@'),
        { Platform: "whatsapp", Sender: { Length: > 0 } number } => number.StartsWith('+') ? number : "+" + number,
        _ => null,
    };

    public static string Channel(string channel) => channel switch
    {
        "whatsapp" => "WhatsApp",
        "instagram" => "Instagram",
        _ => channel
    };
}
