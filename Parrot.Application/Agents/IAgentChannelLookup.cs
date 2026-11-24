namespace Parrot.Application.Agents;

public interface IAgentChannelLookup
{
    /// <summary>
    /// True when a turned-on, non-draft agent answers for the workspace on the connection the message came in on,
    /// or, where agents are kept by channel kind, on <paramref name="channel"/>.
    /// </summary>
    Task<bool> IsAgentLiveAsync(string workspaceId, string channel, Guid? connectionId, CancellationToken ct);
}
