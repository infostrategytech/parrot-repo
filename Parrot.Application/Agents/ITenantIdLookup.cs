namespace Parrot.Application.Agents;

public interface ITenantIdLookup
{
    /// <summary>
    /// Returns the root user account that owns <paramref name="workspaceId"/>,
    /// or null when the workspace does not exist.
    /// </summary>
    Task<string?> FindOwnerIdAsync(string workspaceId, CancellationToken ct);
}
