using Microsoft.Extensions.Caching.Memory;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.Teams;

/// <summary>
/// Answers what a person can do in a workspace. Asked on every request, so
/// answers are kept for a few seconds; a change to the team forgets the
/// person's answers at once on this instance, and other instances catch up
/// within <see cref="WorkspaceAccessService.Lifetime"/>.
/// </summary>
public interface IWorkspaceAccessService
{
    /// <summary>Null when the person neither owns the workspace nor holds a grant that reaches it.</summary>
    Task<WorkspaceAccess?> GetAsync(string userId, string workspaceId);

    /// <summary>Forgets every answer about the person in the owner's workspaces.</summary>
    Task ForgetAsync(string ownerId, string userId);

    /// <summary>Forgets one answer, e.g. the owner's after renaming the workspace, so their next read has the new name.</summary>
    void Forget(string workspaceId, string userId);
}

/// <summary>
/// Tells the other API instances which cached access answers to forget, so a
/// removal or change takes effect everywhere at once rather than within
/// <see cref="WorkspaceAccessService.Lifetime"/>. Best effort: if it cannot
/// reach them, their answers still expire on their own.
/// </summary>
public interface IAccessCacheBroadcast
{
    void Publish(IReadOnlyList<string> cacheKeys);
}

/// <summary>One instance only, or no broadcast configured: forgetting locally is all there is.</summary>
public sealed class NoAccessCacheBroadcast : IAccessCacheBroadcast
{
    public void Publish(IReadOnlyList<string> cacheKeys)
    {
    }
}

public sealed class WorkspaceAccessService(
    IWorkspaceRepository workspaces,
    ITeamRepository team,
    IWorkspaceConnectionDirectory connections,
    IMemoryCache cache,
    IAccessCacheBroadcast broadcast) : IWorkspaceAccessService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    // A box, so "no access" is remembered as well as access.
    private sealed record Entry(WorkspaceAccess? Access);

    public async Task<WorkspaceAccess?> GetAsync(string userId, string workspaceId)
    {
        string key = Key(workspaceId, userId);
        if (cache.TryGetValue(key, out Entry? entry) && entry is not null)
        {
            return entry.Access;
        }

        WorkspaceAccess? access = await ResolveAsync(userId, workspaceId);
        cache.Set(key, new Entry(access), Lifetime);
        return access;
    }

    public async Task ForgetAsync(string ownerId, string userId) =>
        ForgetKeys([.. (await workspaces.ListByOwnerAsync(ownerId)).Select(w => Key(w.Id, userId))]);

    public void Forget(string workspaceId, string userId) => ForgetKeys([Key(workspaceId, userId)]);

    // Here at once, and on every other instance through the broadcast.
    private void ForgetKeys(IReadOnlyList<string> keys)
    {
        foreach (string key in keys)
        {
            cache.Remove(key);
        }

        if (keys.Count > 0)
        {
            broadcast.Publish(keys);
        }
    }

    private async Task<WorkspaceAccess?> ResolveAsync(string userId, string workspaceId)
    {
        Workspace? workspace = await workspaces.GetByIdAsync(workspaceId);
        if (workspace is null)
        {
            return null;
        }

        // A copy, so the cached answer is not an entity some request could change.
        Workspace snapshot = new()
        {
            Id = workspace.Id,
            OwnerId = workspace.OwnerId,
            Name = workspace.Name,
            LogoUrl = workspace.LogoUrl,
            CreatedAt = workspace.CreatedAt,
            UpdatedAt = workspace.UpdatedAt,
        };

        if (workspace.OwnerId == userId)
        {
            return WorkspaceAccess.ForOwner(snapshot);
        }

        WorkspaceGrant? grant = PickGrant(await team.ListGrantsAsync(workspace.OwnerId, userId), workspaceId);
        if (grant is null)
        {
            return null;
        }

        // The role's permissions as they are now, so editing a role reaches everyone holding it.
        (string name, IReadOnlyList<string> permissions) = grant.CustomRoleId is { } roleId
            ? await team.GetRoleAsync(workspace.OwnerId, roleId) is { } custom
                ? (custom.Name, custom.Permissions)
                : ("Removed role", [])
            : WorkspaceRoles.FindById(grant.SystemRole) is { } builtIn
                ? (builtIn.Name, builtIn.Permissions)
                : ("Removed role", []);

        IReadOnlyList<(Guid Id, MessagingPlatform Platform)> listed = grant.Channels.Count == 0
            ? []
            : await connections.ListAsync(workspaceId);
        return WorkspaceAccess.ForGrant(snapshot, grant, name, permissions, listed);
    }

    /// <summary>The workspace's own grant when there is one; otherwise the one for all workspaces.</summary>
    public static WorkspaceGrant? PickGrant(IReadOnlyList<WorkspaceGrant> grants, string workspaceId) =>
        grants.FirstOrDefault(g => g.WorkspaceId == workspaceId) ?? grants.FirstOrDefault(g => g.WorkspaceId is null);

    /// <summary>The cache key of one answer. Only ever starts with "workspace-access:", which a receiver checks.</summary>
    public static string Key(string workspaceId, string userId) => $"{KeyPrefix}{workspaceId}:{userId}";

    public const string KeyPrefix = "workspace-access:";
}
