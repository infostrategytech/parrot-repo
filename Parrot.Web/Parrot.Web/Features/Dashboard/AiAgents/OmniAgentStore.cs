using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// The agents as the AI service keeps them (/api/v1/agents), with the channels they answer on
/// (/api/v1/channels). The screens switch channels on by kind (WhatsApp, Instagram); the service
/// assigns connections, so a kind switched on assigns every connection of that kind the workspace
/// has (Parrot's /api/connections), and switched off unlinks them.
/// With OmniAi:MultiAgentEnabled, several agents can answer on one connection, so a link is named by both
/// the connection and the agent, and only this agent's own links are changed. Off, a connection answers
/// for one agent only: one another agent has is refused before anything is saved, and a link is named by
/// its connection alone, as the AI service had it before.
/// A refusal is the service's ApiException, with its own words for the owner, except a channel held
/// elsewhere: that never says where.
/// </summary>
public sealed class OmniAgentStore(OmniAiClient omni, HttpClient parrot, AuthState auth, IOptions<OmniAiSettings> settings) : IAgentStore
{
    /// <summary>What the owner is told when the service won't link a channel it says is held elsewhere.</summary>
    public const string ChannelUnavailable = "That channel can't take this agent right now. Try again, and contact support if it keeps happening.";

    // The service's code for a channel held elsewhere (another workspace; before agents could share, another agent).
    private const string ChannelTakenCode = "CHANNEL_TAKEN";

    // The version each agent was last read or saved at, sent back as If-Match.
    private readonly Dictionary<string, int> _versions = [];
    // Names for the refusal while agents can't share a channel.
    private readonly Dictionary<string, string> _names = [];

    private bool SharesChannels => settings.Value.MultiAgentEnabled;

    public async Task<IReadOnlyList<AiAgent>> ListAsync(CancellationToken cancellationToken = default)
    {
        var agents = await omni.ListAllAsync<OmniAgent>("api/v1/agents", cancellationToken);
        var channels = await omni.ListAllAsync<OmniChannel>("api/v1/channels", cancellationToken);
        foreach (var agent in agents) Remember(agent);
        // The screens want newest last.
        return agents.OrderBy(a => a.CreatedAt)
            .Select(a => OmniAgentMapping.ToAgent(a, channels.Where(c => c.AgentId == a.AgentId && c.Enabled).Select(c => c.Platform)))
            .ToList();
    }

    public async Task<AiAgent> CreateAsync(AiAgent agent, CancellationToken cancellationToken = default)
    {
        var plan = await PlanChannelsAsync(null, agent, cancellationToken);
        var saved = await WithoutSayingWhereAsync(() => omni.SendAsync<OmniAgent>(HttpMethod.Post, "api/v1/agents",
            OmniAgentMapping.ToCreate(agent, plan.ToAssign.Select(c => c.Id)), idempotencyKey: Guid.NewGuid().ToString("N"),
            cancellationToken: cancellationToken));
        Remember(saved);
        var result = OmniAgentMapping.ToAgent(saved, plan.ToAssign.Select(c => c.Channel));
        // What the service does not keep stays as it was entered.
        result.Playbooks = agent.Playbooks;
        result.Settings.NoteFiles = [.. agent.Settings.NoteFiles];
        return result;
    }

    public async Task SaveAsync(AiAgent agent, CancellationToken cancellationToken = default)
    {
        var plan = await PlanChannelsAsync(agent.Id, agent, cancellationToken);
        var path = AgentPath(agent.Id);
        var version = await VersionAsync(agent.Id, cancellationToken);
        Remember(await omni.SendAsync<OmniAgent>(HttpMethod.Patch, path, OmniAgentMapping.ToUpdate(agent), version,
            cancellationToken: cancellationToken));

        foreach (var connection in plan.ToAssign)
            await WithoutSayingWhereAsync(() => omni.SendAsync<OmniChannel>(HttpMethod.Post, "api/v1/channels",
                new AssignChannel(agent.Id, connection.Id), cancellationToken: cancellationToken));
        foreach (OmniChannel channel in plan.ToResume)
            await omni.SendAsync<OmniChannel>(HttpMethod.Patch, ChannelPath(channel.ConnectionId, agent.Id),
                new ResumeChannel(true), channel.Version, cancellationToken: cancellationToken);
        foreach (OmniChannel channel in plan.ToUnlink)
            await omni.SendAsync<ChannelDeleted>(HttpMethod.Delete, ChannelPath(channel.ConnectionId, agent.Id), null,
                channel.Version, cancellationToken: cancellationToken);
    }

    public async Task SetLiveAsync(string agentId, bool on, CancellationToken cancellationToken = default)
    {
        var version = await VersionAsync(agentId, cancellationToken);
        Remember(await omni.SendAsync<OmniAgent>(HttpMethod.Patch, $"{AgentPath(agentId)}/live", new SetLive(on), version,
            cancellationToken: cancellationToken));
    }

    public async Task DeleteAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await omni.SendAsync<AgentDeleted>(HttpMethod.Delete, AgentPath(agentId), null, cancellationToken: cancellationToken);
        _versions.Remove(agentId);
        _names.Remove(agentId);
    }

    // What has to change for the agent to answer on exactly the kinds switched on. Other agents on the same
    // connections are left as they are when agents can share them; otherwise one there stops the save before
    // anything is written, not half of it.
    private async Task<ChannelPlan> PlanChannelsAsync(string? agentId, AiAgent agent, CancellationToken cancellationToken)
    {
        HashSet<string> wanted = agent.Channels.Where(c => c.IsEnabled).Select(c => c.Key.ToLowerInvariant()).ToHashSet();
        // A new agent has no links yet, and only another agent's links can stop it.
        List<OmniChannel> assigned = agentId is null && SharesChannels ? [] : await omni.ListAllAsync<OmniChannel>("api/v1/channels", cancellationToken);
        List<OmniChannel> own = assigned.Where(c => agentId is not null && c.AgentId == agentId).ToList();
        List<ParrotConnection> connections = wanted.Count == 0 ? [] : await ConnectionsAsync(cancellationToken);
        // The service refuses a second link for the same agent and connection, so there is one at most.
        Dictionary<Guid, OmniChannel> byConnection = own.ToDictionary(c => c.ConnectionId);

        ChannelPlan plan = new();
        foreach (ParrotConnection connection in connections.Where(c => wanted.Contains(c.Channel)))
        {
            if (!SharesChannels && assigned.FirstOrDefault(c => c.ConnectionId == connection.Id && c.AgentId != agentId) is { } other)
                throw Taken(connection.Channel, other.AgentId);
            if (!byConnection.TryGetValue(connection.Id, out OmniChannel? channel))
                plan.ToAssign.Add(connection);
            else if (!channel.Enabled)
                plan.ToResume.Add(channel);
        }

        plan.ToUnlink.AddRange(own.Where(c => !wanted.Contains(c.Platform)));
        return plan;
    }

    // Another agent of this workspace answers there, so naming it tells the owner nothing new.
    private ApiException Taken(string kind, string otherAgentId)
    {
        string name = AgentBuilderCopy.DefaultChannels([]).FirstOrDefault(c => c.Key == kind)?.Name ?? kind;
        string other = _names.TryGetValue(otherAgentId, out string? known) ? known : "another agent";
        return new ApiException(HttpStatusCode.Conflict,
            $"{other} already answers on {name}. Switch it off there first, then try again.", ChannelTakenCode, []);
    }

    // The service says a channel is held elsewhere (in another workspace): the owner is told it can't be used,
    // never that it exists somewhere else. The service keeps the detail under its trace id.
    private static async Task<T> WithoutSayingWhereAsync<T>(Func<Task<T>> send)
    {
        try
        {
            return await send();
        }
        catch (ApiException exception) when (exception.ErrorCode == ChannelTakenCode)
        {
            throw new ApiException(exception.StatusCode ?? HttpStatusCode.Conflict, ChannelUnavailable, ChannelTakenCode, []);
        }
    }

    // The workspace's connections, from Parrot (the AI service only knows assigned ones).
    private async Task<List<ParrotConnection>> ConnectionsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/connections");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to manage agents."));
        using var response = await parrot.SendAsync(request, cancellationToken);
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<List<ParrotConnection>>(cancellationToken) ?? [];
    }

    private async Task<int> VersionAsync(string agentId, CancellationToken cancellationToken)
    {
        if (_versions.TryGetValue(agentId, out var version)) return version;
        var agent = await omni.GetAsync<OmniAgent>(AgentPath(agentId), cancellationToken);
        Remember(agent);
        return agent.Version;
    }

    private void Remember(OmniAgent agent)
    {
        _versions[agent.AgentId] = agent.Version;
        _names[agent.AgentId] = agent.Name;
    }

    private static string AgentPath(string agentId) => $"api/v1/agents/{Uri.EscapeDataString(agentId)}";

    // One agent's link on one connection; the connection alone is refused once several agents share it.
    private string ChannelPath(Guid connectionId, string agentId) => SharesChannels
        ? $"api/v1/channels/{connectionId}/agents/{Uri.EscapeDataString(agentId)}"
        : $"api/v1/channels/{connectionId}";

    private sealed class ChannelPlan
    {
        public List<ParrotConnection> ToAssign { get; } = [];
        public List<OmniChannel> ToResume { get; } = [];
        public List<OmniChannel> ToUnlink { get; } = [];
    }

    private sealed record AssignChannel(string AgentId, Guid ConnectionId);

    private sealed record ResumeChannel(bool Enabled);

    private sealed record SetLive(bool IsLive);

    private sealed record ChannelDeleted(Guid ConnectionId, bool Deleted);

    private sealed record AgentDeleted(string AgentId, bool Deleted);

    private sealed record ParrotConnection(Guid Id, string Channel);
}
