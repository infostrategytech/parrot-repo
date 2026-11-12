using Parrot.Application.Contracts.BusinessSources;

namespace Parrot.Application.BusinessSources;

public interface IBusinessSourceCredentialsService
{
    /// <summary>
    /// Returns the access token and API version for the given source, scoped to the workspace.
    /// Throws NotFoundException when no active source matches or when it belongs to a different workspace —
    /// both cases return the same error so source ids cannot be enumerated.
    /// Throws ConflictException when the connection has no access token configured yet.
    /// </summary>
    Task<BusinessSourceCredentialsResponse> GetCredentialsAsync(Guid sourceId, string workspaceId, CancellationToken ct = default);
}
