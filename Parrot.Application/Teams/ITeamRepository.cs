using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.Teams;

/// <summary>An owner's team: the grants its people hold, and the invitations it has sent.</summary>
public interface ITeamRepository
{
    /// <summary>One person's grants in one owner's team.</summary>
    Task<IReadOnlyList<WorkspaceGrant>> ListGrantsAsync(string ownerId, string userId);

    /// <summary>Every grant in the owner's team, earliest first.</summary>
    Task<IReadOnlyList<WorkspaceGrant>> ListTeamGrantsAsync(string ownerId);

    /// <summary>Every grant the person holds, in any owner's team.</summary>
    Task<IReadOnlyList<WorkspaceGrant>> ListGrantsForUserAsync(string userId);

    /// <summary>Replaces the person's grants in the owner's team with these. Empty removes them from the team.</summary>
    Task ReplaceGrantsAsync(string ownerId, string userId, IReadOnlyList<WorkspaceGrant> grants);

    /// <summary>The owner's own roles, by name.</summary>
    Task<IReadOnlyList<OwnerRole>> ListRolesAsync(string ownerId);

    Task<OwnerRole?> GetRoleAsync(string ownerId, Guid roleId);

    /// <summary>False when the owner already has a role by that name, and nothing is saved.</summary>
    Task<bool> TryAddRoleAsync(OwnerRole role);

    /// <summary>False when the new name is taken by another of the owner's roles, and nothing is saved.</summary>
    Task<bool> TryUpdateRoleAsync(OwnerRole role);

    Task DeleteRoleAsync(OwnerRole role);

    /// <summary>The people holding a grant with this role.</summary>
    Task<IReadOnlyList<string>> ListRoleHoldersAsync(string ownerId, Guid roleId);

    Task<TeamInvite?> GetInviteAsync(string ownerId, Guid inviteId);

    Task<TeamInvite?> FindInviteByTokenHashAsync(string tokenHash);

    /// <summary>The address's invitation that has not been accepted, revoked or let expire, if there is one.</summary>
    Task<TeamInvite?> FindPendingInviteAsync(string ownerId, string normalizedEmail, DateTime now);

    /// <summary>Invitations still waiting for an answer, newest first.</summary>
    Task<IReadOnlyList<TeamInvite>> ListPendingInvitesAsync(string ownerId, DateTime now);

    Task AddInviteAsync(TeamInvite invite);

    Task UpdateInviteAsync(TeamInvite invite);
}

/// <summary>A workspace's channel connections, connected or not, so a channel limit can be turned into connection ids.</summary>
public interface IWorkspaceConnectionDirectory
{
    Task<IReadOnlyList<(Guid Id, MessagingPlatform Platform)>> ListAsync(string workspaceId);
}
