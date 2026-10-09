using System.Diagnostics;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Agents;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Agents;

/// <summary>
/// The owner's agents: what they are called, where they answer and how they
/// are set up. Every change is saved to the row first and then announced on
/// the bus, so the orchestrator drops the configuration it cached. The
/// announcement is best effort: the row is what the orchestrator reads on
/// its next cache miss anyway, so a missed event costs at most one cache
/// lifetime of the old settings, never the save.
/// </summary>
public class AgentService : IAgentService
{
    // A channel key is a short lower-case word ("whatsapp"). Anything else is
    // a typo or a body the screens never send.
    private const int MaxChannelKeyLength = 32;

    private readonly IAgentRepository _repository;
    private readonly IMessageBus _bus;
    private readonly KafkaSettings _kafka;
    private readonly IAppLogger<AgentService> _logger;

    public AgentService(IAgentRepository repository, IMessageBus bus, IOptions<KafkaSettings> kafka, IAppLogger<AgentService> logger)
    {
        _repository = repository;
        _bus = bus;
        _kafka = kafka.Value;
        _logger = logger;
    }

    public async Task<AgentResponse> CreateAsync(string userId, AgentInfo request)
    {
        Validate(request);

        Agent agent = new()
        {
            UserId = userId,
            Name = request.Name.Trim(),
            Description = request.Description,
            MascotUrl = request.MascotUrl,
            SupportsHumanTakeover = request.SupportsHumanTakeover,
            Channels = NormalizeChannels(request.Channels),
            SettingsJson = AgentSettingsJson.Write(request.Settings),
            IsDraft = request.IsDraft,
            // A draft answers no one; anything else is on from the start.
            IsActive = !request.IsDraft,
        };

        await _repository.AddAsync(agent);
        await AnnounceAsync(agent);
        return ToResponse(agent);
    }

    public async Task<IReadOnlyList<AgentResponse>> GetAllAsync(string userId)
    {
        IReadOnlyList<Agent> agents = await _repository.GetByUserIdAsync(userId);
        return agents.Select(ToResponse).ToList();
    }

    public async Task<AgentResponse> GetByIdAsync(string userId, Guid agentId) =>
        ToResponse(await OwnedAsync(userId, agentId));

    public async Task<AgentResponse> UpdateAsync(string userId, Guid agentId, AgentInfo request)
    {
        Validate(request);
        Agent agent = await OwnedAsync(userId, agentId);

        agent.Name = request.Name.Trim();
        agent.Description = request.Description;
        agent.MascotUrl = request.MascotUrl;
        agent.SupportsHumanTakeover = request.SupportsHumanTakeover;
        agent.Channels = NormalizeChannels(request.Channels);
        agent.SettingsJson = AgentSettingsJson.Write(request.Settings);
        Touch(agent);

        await _repository.UpdateAsync(agent);
        await AnnounceAsync(agent);
        return ToResponse(agent);
    }

    public async Task<AgentResponse> SetLiveAsync(string userId, Guid agentId, bool on)
    {
        Agent agent = await OwnedAsync(userId, agentId);

        agent.IsActive = on;
        // The owner has decided about it, so it is a draft no longer, whichever way the switch went.
        agent.IsDraft = false;
        Touch(agent);

        await _repository.UpdateAsync(agent);
        await AnnounceAsync(agent);
        return ToResponse(agent);
    }

    public async Task DeleteAsync(string userId, Guid agentId)
    {
        Agent agent = await OwnedAsync(userId, agentId);

        agent.IsActive = false;
        agent.DeletedAt = DateTime.UtcNow;
        Touch(agent);

        await _repository.UpdateAsync(agent);
        await AnnounceAsync(agent, deleted: true);
    }

    public async Task<AgentResponse?> ResolveForChannelAsync(string ownerUserId, string channel)
    {
        string key = channel.Trim().ToLowerInvariant();
        if (key.Length == 0) return null;

        Agent? agent = await _repository.GetLiveForChannelAsync(ownerUserId, key);
        return agent is null ? null : ToResponse(agent);
    }

    /// <summary>True when the agent answers customers: turned on, not a draft, and not deleted.</summary>
    public static bool IsLive(Agent agent) => agent.IsActive && !agent.IsDraft && agent.DeletedAt is null;

    private static void Validate(AgentInfo request)
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("Give the agent a name.");
        else if (request.Name.Trim().Length > 256) errors.Add("The name can be at most 256 characters.");
        if (request.Description is null || request.Description.Length > 2000) errors.Add("The description can be at most 2000 characters.");
        if (string.IsNullOrWhiteSpace(request.MascotUrl) || request.MascotUrl.Length > 2048) errors.Add("Choose a look for the agent.");

        foreach (string channel in request.Channels ?? [])
        {
            string key = channel.Trim();
            if (key.Length == 0 || key.Length > MaxChannelKeyLength || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                errors.Add($"'{channel}' is not a channel.");
        }

        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private static List<string> NormalizeChannels(IEnumerable<string>? channels) =>
        (channels ?? [])
            .Select(c => c.Trim().ToLowerInvariant())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static void Touch(Agent agent)
    {
        agent.Version++;
        agent.UpdatedAt = DateTime.UtcNow;
    }

    private async Task<Agent> OwnedAsync(string userId, Guid agentId)
    {
        Agent agent = await _repository.GetByIdAsync(agentId)
            ?? throw new NotFoundException($"Agent {agentId} not found.");

        if (agent.DeletedAt is not null)
            throw new NotFoundException($"Agent {agentId} not found.");

        if (agent.UserId != userId)
            throw new ForbiddenException("You do not have access to this agent.");

        return agent;
    }

    private async Task AnnounceAsync(Agent agent, bool deleted = false)
    {
        AgentConfigChangedEvent changed = new()
        {
            TenantId = agent.UserId,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = Activity.Current?.TraceId.ToString(),
            Payload = new AgentConfigChangedPayload
            {
                AgentId = agent.Id.ToString(),
                Version = agent.Version,
                Channels = agent.Channels.ToList(),
                IsLive = IsLive(agent),
                Deleted = deleted,
            },
        };

        try
        {
            // Keyed by tenant, as the orchestrator keys its own events, so one tenant's events stay in order.
            await _bus.PublishAsync(_kafka.AgentEventsTopic, agent.UserId, changed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "agent.config.announce_failed AgentId={AgentId} Version={Version}; the orchestrator reads the saved row on its next cache miss",
                agent.Id,
                agent.Version);
        }
    }

    private static AgentResponse ToResponse(Agent agent) => new()
    {
        Id = agent.Id,
        Name = agent.Name,
        Description = agent.Description,
        MascotUrl = agent.MascotUrl,
        SupportsHumanTakeover = agent.SupportsHumanTakeover,
        IsActive = agent.IsActive,
        Channels = agent.Channels.ToList(),
        Settings = AgentSettingsJson.Read(agent.SettingsJson),
        IsDraft = agent.IsDraft,
        Version = agent.Version,
        CreatedAt = agent.CreatedAt,
        UpdatedAt = agent.UpdatedAt,
    };
}
