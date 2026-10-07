using Parrot.Web.Components.Pages.Dashboard.AiAgents;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// Where the business's agents are kept. Every screen reaches agents through
/// <see cref="State.AiAgentsState"/>, which reaches them only through this, so
/// connecting the agents API means implementing this one interface and
/// registering it in Program.cs; no screen changes.
///
/// Each call either completes or throws (HttpRequestException, a timeout, a
/// JSON error); the screens already say so and offer to try again. What the
/// API needs to carry is listed in agents-backend-requests.
/// </summary>
public interface IAgentStore
{
    /// <summary>The business's agents, newest last.</summary>
    Task<IReadOnlyList<AiAgent>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves a new agent (a draft, or turned on) and returns it with its id.</summary>
    Task<AiAgent> CreateAsync(AiAgent agent, CancellationToken cancellationToken = default);

    /// <summary>Saves everything about an existing agent: name, look, job, channels and settings.</summary>
    Task SaveAsync(AiAgent agent, CancellationToken cancellationToken = default);

    /// <summary>Turns an agent on (it starts replying where it answers) or off. A draft turned on stops being a draft.</summary>
    Task SetLiveAsync(string agentId, bool on, CancellationToken cancellationToken = default);

    Task DeleteAsync(string agentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Agents kept for the signed-in session only, until the agents API is
/// connected: everything works, and nothing outlives the session. Used in
/// demo mode and, for now, with the real backend too.
/// </summary>
public sealed class SessionAgentStore : IAgentStore
{
    private readonly List<AiAgent> _agents = [];

    public Task<IReadOnlyList<AiAgent>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AiAgent>>(_agents.ToList());

    public Task<AiAgent> CreateAsync(AiAgent agent, CancellationToken cancellationToken = default)
    {
        agent.Id = Guid.NewGuid().ToString();
        _agents.Add(agent);
        return Task.FromResult(agent);
    }

    public Task SaveAsync(AiAgent agent, CancellationToken cancellationToken = default)
    {
        var index = _agents.FindIndex(a => a.Id == agent.Id);
        if (index < 0) throw new HttpRequestException("No such agent.", null, System.Net.HttpStatusCode.NotFound);
        _agents[index] = agent;
        return Task.CompletedTask;
    }

    public Task SetLiveAsync(string agentId, bool on, CancellationToken cancellationToken = default)
    {
        var agent = _agents.FirstOrDefault(a => a.Id == agentId)
            ?? throw new HttpRequestException("No such agent.", null, System.Net.HttpStatusCode.NotFound);
        agent.IsDraft = false;
        agent.IsActive = on;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string agentId, CancellationToken cancellationToken = default)
    {
        _agents.RemoveAll(a => a.Id == agentId);
        return Task.CompletedTask;
    }
}
