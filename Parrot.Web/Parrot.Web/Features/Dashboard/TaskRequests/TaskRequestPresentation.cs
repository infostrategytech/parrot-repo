using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Components.Pages.Dashboard.TaskRequests;

namespace Parrot.Web.Features.Dashboard.TaskRequests;

/// <summary>
/// How Parrot's task-request strings read in Aurora's vocabulary. Parrot keeps
/// its data as plain strings (a status is "Pending", a channel is "Whatsapp
/// Business"); Aurora's screen needs a tone for the badge and a glyph for the
/// channel. This is the one place those translations live.
/// </summary>
public static class TaskRequestPresentation
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    /// <summary>Parrot's three statuses, in the order the status menu lists them.</summary>
    public static readonly string[] Statuses = [Pending, Completed, Cancelled];

    public sealed record FilterOption(string Value, string Label);

    // The same channel and agent lists the previous page filtered by. They are
    // Parrot's data, not a roster Aurora knows about, so they stay verbatim.
    public static readonly FilterOption[] Channels =
    [
        new("Whatsapp Business", "WhatsApp"),
        new("Instagram", "Instagram"),
        new("Facebook Marketplace", "Facebook")
    ];

    public static readonly FilterOption[] Agents =
    [
        new("BakerBot", "BakerBot"),
        new("Bakerbot", "Bakerbot")
    ];

    /// <summary>
    /// Pending is the customer-or-team-owes-a-step state, which Aurora paints
    /// amber; the two closed states take success and danger as Aurora's
    /// completed and cancelled do.
    /// </summary>
    public static StatusTone ToneFor(string status) => status switch
    {
        Completed => StatusTone.Success,
        Cancelled => StatusTone.Danger,
        _ => StatusTone.Warning
    };

    public static IconName ChannelGlyph(string channelIcon) => channelIcon switch
    {
        "whatsapp" => IconName.RiWhatsappFill,
        "instagram" => IconName.RiInstagramFill,
        "facebook" => IconName.RiFacebookCircleFill,
        _ => IconName.RiChat3Fill
    };

    /// <summary>The modifier class Aurora's ChannelGlyph uses for the channel's tint.</summary>
    public static string ChannelClass(string channelIcon) =>
        "channel-glyph channel-glyph--" + (string.IsNullOrWhiteSpace(channelIcon) ? "web" : channelIcon);

    public static string ChannelFilterLabel(string? value) =>
        value is null ? "All channels" : Channels.FirstOrDefault(c => c.Value == value)?.Label ?? value;

    public static string AgentFilterLabel(string? value) =>
        value is null ? "All agents" : Agents.FirstOrDefault(a => a.Value == value)?.Label ?? value;

    /// <summary>
    /// Parrot stores an activity entry as one string with the headline on the
    /// first line and any detail after a line break. Aurora's timeline wants
    /// them apart.
    /// </summary>
    public static (string Title, string? Detail) SplitActivity(TaskActivityLog entry)
    {
        var parts = entry.Description.Split('\n', 2, StringSplitOptions.TrimEntries);
        return (parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }
}
