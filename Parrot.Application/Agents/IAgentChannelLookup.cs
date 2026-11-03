namespace Parrot.Application.Agents;

public interface IAgentChannelLookup
{
    /// <summary>True when a turned-on, non-draft agent covers <paramref name="channel"/> for the workspace.</summary>
    Task<bool> IsAgentLiveAsync(string workspaceId, string channel, CancellationToken ct);
}
