using Parrot.Application.DTOs.Workspaces;
using Parrot.Application.Models;
using Parrot.Domain.Entities;

namespace Parrot.Application.Workspaces;

/// <summary>
/// The workspaces a tenant owns. Every call is made as a user, and a workspace
/// that user does not own is answered as not found, so an id cannot be used to
/// learn that another tenant's workspace exists.
/// </summary>
public interface IWorkspaceService
{
    Task<IReadOnlyList<WorkspaceResponse>> ListAsync(string userId, string currentWorkspaceId);

    Task<WorkspaceResponse> CreateAsync(string userId, CreateWorkspaceRequest request);

    Task<WorkspaceResponse> UpdateAsync(string userId, string workspaceId, UpdateWorkspaceRequest request);

    /// <summary>The workspace, when <paramref name="userId"/> owns it. Otherwise a NotFoundException.</summary>
    Task<Workspace> GetOwnedAsync(string userId, string workspaceId);

    /// <summary>
    /// Where a sign-in lands: the workspace switched to last, else the first one
    /// the tenant made, else a first workspace made now from the account.
    /// </summary>
    Task<Workspace> ResolveForSignInAsync(ApplicationUser user);
}
