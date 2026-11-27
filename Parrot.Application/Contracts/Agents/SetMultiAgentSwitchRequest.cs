namespace Parrot.Application.Contracts.Agents;

/// <summary>Turns agents sharing a number on or off for the caller's workspace.</summary>
public sealed record SetMultiAgentSwitchRequest(bool IsOn);
