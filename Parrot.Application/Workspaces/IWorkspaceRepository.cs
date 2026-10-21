using Parrot.Domain.Entities;

namespace Parrot.Application.Workspaces;

public interface IWorkspaceRepository
{
    Task<Workspace?> GetByIdAsync(string id);

    /// <summary>The owner's workspaces, oldest first, so the first is the one they started with.</summary>
    Task<IReadOnlyList<Workspace>> ListByOwnerAsync(string ownerId);

    /// <summary>
    /// Saves a new workspace. False when one with the same id is already there —
    /// two sign-ins racing to create a tenant's first workspace — and nothing is saved.
    /// </summary>
    Task<bool> TryAddAsync(Workspace workspace);

    Task UpdateAsync(Workspace workspace);
}
