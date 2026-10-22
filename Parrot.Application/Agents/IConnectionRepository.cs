namespace Parrot.Application.Agents;

/// <summary>A connection resolved to its workspace, owner and channel type.</summary>
public sealed record ConnectionInfo(Guid Id, string WorkspaceId, string OwnerId, string ChannelKey, bool IsDisabled);

public interface IConnectionRepository
{
    /// <summary>
    /// Finds a connection across all channel tables and returns its workspace,
    /// owner and channel key. Returns null when no connection has that id.
    /// </summary>
    Task<ConnectionInfo?> FindAsync(Guid id, CancellationToken cancellationToken = default);
}
