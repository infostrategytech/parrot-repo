using Parrot.Web.Features.Dashboard.Channels.Models;

namespace Parrot.Web.Features.Dashboard.Channels.Services;

/// <summary>
/// What each channel can be connected with right now. The Channels screens
/// read only this to decide what to offer: a sign-in window (Meta's Embedded
/// Signup for WhatsApp, Facebook Login, Instagram Login, X), a manual setup,
/// or "Coming soon". When the API adds a way to connect a channel, it is
/// switched on here; the screens follow.
/// </summary>
public interface IChannelCapabilities
{
    /// <summary>The provider's own window can connect it (a route card that works).</summary>
    bool CanUseWindow(ChannelId channel);

    /// <summary>It has a manual setup: WhatsApp's guided one, or Facebook's two sections.</summary>
    bool CanSetUpManually(ChannelId channel);

    /// <summary>
    /// Once connected, it can be disconnected (messages stop arriving). Without
    /// it the card offers no Disconnect, so it never says "Not connected" while
    /// messages still come in.
    /// </summary>
    bool CanDisconnect(ChannelId channel);

    /// <summary>
    /// Whether the channel is on the page at all. Every channel is, unless a
    /// set of capabilities leaves one out (the demo leaves out a channel whose
    /// setup is still to be designed).
    /// </summary>
    bool IsListed(ChannelId channel) => true;
}

/// <summary>
/// With the real backend today: WhatsApp, Facebook, TikTok and Instagram
/// connect manually (the API's webhook and credential calls). Meta's Embedded
/// Signup needs Tech Provider approval, and there is no Facebook Login,
/// Instagram Login or X connection yet, so those say "Coming soon". All four
/// manual channels have a disconnect call.
/// </summary>
public sealed class LiveChannelCapabilities : IChannelCapabilities
{
    public bool CanUseWindow(ChannelId channel) => false;

    public bool CanSetUpManually(ChannelId channel) =>
        channel is ChannelId.Whatsapp or ChannelId.Facebook or ChannelId.TikTok or ChannelId.Instagram;

    public bool CanDisconnect(ChannelId channel) =>
        channel is ChannelId.Whatsapp or ChannelId.Facebook or ChannelId.TikTok or ChannelId.Instagram;
}
