using Parrot.Domain.Enums;

namespace Parrot.Application.Teams;

/// <summary>One channel a grant can be limited to. <paramref name="Code"/> is how a session token spells it.</summary>
public sealed record ChannelInfo(string Key, string Label, string Code);

/// <summary>The channels a grant can be limited to, by platform. The keys are what a grant stores.</summary>
public static class WorkspaceChannels
{
    public const string WhatsApp = "whatsapp";
    public const string Instagram = "instagram";
    public const string Facebook = "facebook";
    public const string TikTok = "tiktok";

    public static readonly IReadOnlyList<ChannelInfo> All =
    [
        new(WhatsApp, "WhatsApp", "wa"),
        new(Instagram, "Instagram", "ig"),
        new(Facebook, "Facebook Messenger", "fb"),
        new(TikTok, "TikTok", "tt"),
    ];

    public static bool IsKnown(string key) => All.Any(c => c.Key == key);

    /// <summary>The channels as a session token carries them, e.g. "wa ig"; "*" for every channel.</summary>
    public static string Abbreviate(WorkspaceAccess access) =>
        access.Channels.Count == 0
            ? WorkspacePermissions.AllCode
            : string.Join(' ', All.Where(c => access.Channels.Contains(c.Key)).Select(c => c.Code));

    public static string KeyOf(MessagingPlatform platform) => platform switch
    {
        MessagingPlatform.WhatsApp => WhatsApp,
        MessagingPlatform.Instagram => Instagram,
        MessagingPlatform.Facebook => Facebook,
        MessagingPlatform.TikTok => TikTok,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
    };
}
