using Parrot.Web.Components.Pages.Dashboard.TeachAi;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.Channels.Models;
using Parrot.Web.Features.Dashboard.Channels.Services;
using Parrot.Web.Features.Dashboard.Channels.State;

namespace Parrot.Web.Components.Pages.Dashboard.AiAgents;

/// <summary>
/// What a business needs before it can set up an agent, in order: a connected
/// channel for it to answer on, then, the first time, what the business is.
/// It asks for a few lines about it ("Tell us about your business", designer
/// and team, 14 October 2026). Every way of creating an agent goes through
/// RequireAsync. Without a channel, the
/// "Connect a channel first" dialog opens instead and nothing else happens.
/// </summary>
public sealed class AgentGate(ChannelStatusLoader loader, ChannelsState channels, AuthState auth, BusinessIntroState intro)
{
    public bool NeedsChannel { get; private set; }

    public event Action? Changed;

    public async Task RequireAsync(Func<Task> then)
    {
        if (!await HasChannelAsync())
        {
            NeedsChannel = true;
            Changed?.Invoke();
            return;
        }
        await intro.RequireAsync(then);
    }

    public void Dismiss()
    {
        NeedsChannel = false;
        Changed?.Invoke();
    }

    /// <summary>The channels an agent can answer on that are connected now, read fresh.</summary>
    public async Task<IReadOnlyList<AgentChannelChoice>> ConnectedAsync()
    {
        if (auth.CurrentUser?.Token is { Length: > 0 } token)
            await loader.RefreshAsync(token, CancellationToken.None);
        var supported = AgentBuilderCopy.Channels.ToDictionary(c => c.Key, c => c.Name, StringComparer.OrdinalIgnoreCase);
        return channels.Workspace.Channels
            .Where(channel => channel.Status == ChannelStatus.Connected)
            .Select(channel => (Channel: channel, Info: channels.Channels.First(info => info.Id == channel.Id)))
            .Where(item => supported.ContainsKey(item.Info.Key))
            .Select(item => new AgentChannelChoice(item.Info.Key, supported[item.Info.Key], item.Channel.AccountName))
            .ToList();
    }

    private async Task<bool> HasChannelAsync() => (await ConnectedAsync()).Count > 0;
}
