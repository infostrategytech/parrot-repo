namespace Parrot.Application.Agents;

/// <summary>The channel key and workspace of a validated connection.</summary>
public sealed record ConnectionValidationResult(Guid Id, string ChannelKey, string WorkspaceId);

public interface IConnectionService
{
    /// <summary>
    /// Confirms that <paramref name="connectionId"/> belongs to <paramref name="workspaceId"/>
    /// and returns its channel key. Throws <see cref="Parrot.Domain.Exceptions.ForbiddenException"/>
    /// when the connection belongs to the same tenant but a different workspace, and
    /// <see cref="Parrot.Domain.Exceptions.NotFoundException"/> when it belongs to a different
    /// tenant or does not exist — both cases look identical to the caller.
    /// </summary>
    Task<ConnectionValidationResult> ValidateForWorkspaceAsync(
        Guid connectionId,
        string workspaceId,
        CancellationToken cancellationToken = default);
}
