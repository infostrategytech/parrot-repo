namespace Parrot.Application.DTOs.Agents;

/// <summary>
/// The agent answering one of a tenant's channels, as the orchestrator reads
/// it: enough to build the persona and the policy for a turn, and the
/// version to tell a stale copy from a fresh one.
/// </summary>
public sealed record AgentResolution(
    string AgentId,
    string Name,
    bool IsLive,
    IReadOnlyList<string> Channels,
    AgentSettingsDto Settings,
    int Version,
    string? BusinessName,
    string OwnerUserId);
