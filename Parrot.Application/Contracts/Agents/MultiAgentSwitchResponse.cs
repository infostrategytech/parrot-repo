namespace Parrot.Application.Contracts.Agents;

/// <summary>
/// The workspace's switch for agents sharing a number (temporary, for the live test).
/// IsOffered: this workspace is in the test. IsOn: several agents may share its numbers now.
/// </summary>
public sealed record MultiAgentSwitchResponse(bool IsOffered, bool IsOn);
