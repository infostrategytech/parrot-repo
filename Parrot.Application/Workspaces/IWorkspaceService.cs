using Parrot.Application.DTOs.Workspaces;
using Parrot.Application.Models;
using Parrot.Application.Teams;
using Parrot.Domain.Entities;

namespace Parrot.Application.Workspaces;

/// <summary>
/// The workspaces a person owns or has joined. Every call is made as a user,
/// and a workspace that user cannot reach is answered as not found, so an id
/// cannot be used to learn that another tenant's workspace exists.
/// </summary>
public interface IWorkspaceService
{
    /// <param name="defaultWorkspaceId">The person's sign-in default, to mark it; null when they resume.</param>
    Task<IReadOnlyList<WorkspaceResponse>> ListAsync(string userId, string currentWorkspaceId, string? defaultWorkspaceId = null);

    Task<WorkspaceResponse> CreateAsync(string userId, CreateWorkspaceRequest request);

    Task<WorkspaceResponse> UpdateAsync(string userId, string workspaceId, UpdateWorkspaceRequest request);

    /// <summary>The workspace, when <paramref name="userId"/> owns it. Otherwise a NotFoundException.</summary>
    Task<Workspace> GetOwnedAsync(string userId, string workspaceId);

    /// <summary>What the user can do in the workspace, as its owner or a member. Otherwise a NotFoundException.</summary>
    Task<WorkspaceAccess> GetAccessibleAsync(string userId, string workspaceId);

    /// <summary>
    /// Where a sign-in lands: the person's chosen default, else the workspace
    /// switched to last, each while it can still be reached; else the first one
    /// the tenant made, else the first one they joined, else a first workspace
    /// made now from the account.
    /// </summary>
    Task<WorkspaceAccess> ResolveForSignInAsync(ApplicationUser user);
}
