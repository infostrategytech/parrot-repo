using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Agents;

public class ConnectionService : IConnectionService
{
    private readonly IConnectionRepository _connections;
    private readonly IWorkspaceRepository _workspaces;

    public ConnectionService(IConnectionRepository connections, IWorkspaceRepository workspaces)
    {
        _connections = connections;
        _workspaces = workspaces;
    }

    public async Task<ConnectionValidationResult> ValidateForWorkspaceAsync(
        Guid connectionId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ConnectionInfo? connection = await _connections.FindAsync(connectionId, cancellationToken);

        if (connection is null)
            throw new NotFoundException("Connection not found.");

        if (connection.WorkspaceId == workspaceId)
            return new ConnectionValidationResult(connection.Id, connection.ChannelKey, connection.WorkspaceId);

        // The connection belongs to a different workspace. Determine whether the caller
        // owns that workspace (same tenant) or not, so we can return 403 vs 404 correctly.
        Workspace? callerWorkspace = await _workspaces.GetByIdAsync(workspaceId)
            ?? throw new NotFoundException("Connection not found.");

        if (connection.OwnerId == callerWorkspace.OwnerId)
        {
            // Same tenant, different workspace — the caller knows the connection exists but may not use it.
            throw new ForbiddenException("You do not have access to this connection.");
        }

        // Different tenant — treat as not found so callers cannot enumerate foreign connections.
        throw new NotFoundException("Connection not found.");
    }
}
