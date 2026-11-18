using Microsoft.Extensions.Caching.Memory;
using Moq;
using Parrot.Api.Realtime;
using Parrot.Application.BusinessSources;
using Parrot.Application.Logging;
using Parrot.Application.Workspaces;
using Parrot.Application.Teams;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Services.Teams;

/// <summary>Grants and invites in lists, answering as the database does.</summary>
public sealed class FakeTeamRepository : ITeamRepository
{
    public List<WorkspaceGrant> Grants { get; } = [];

    public List<TeamInvite> Invites { get; } = [];

    public List<OwnerRole> Roles { get; } = [];

    /// <summary>A grant with a built-in role, named as people see it, e.g. WorkspaceRoles.Analyst.</summary>
    public WorkspaceGrant Grant(string ownerId, string userId, string? workspaceId, string roleName, IEnumerable<string>? channels = null)
    {
        WorkspaceGrant grant = new()
        {
            OwnerId = ownerId,
            UserId = userId,
            WorkspaceId = workspaceId,
            SystemRole = WorkspaceRoles.FindByName(roleName)!.Id,
            Channels = [.. channels ?? []],
        };
        Grants.Add(grant);
        return grant;
    }

    /// <summary>A grant with one of the owner's own roles.</summary>
    public WorkspaceGrant Grant(string userId, string? workspaceId, OwnerRole role, IEnumerable<string>? channels = null)
    {
        WorkspaceGrant grant = new() { OwnerId = role.OwnerId, UserId = userId, WorkspaceId = workspaceId, CustomRoleId = role.Id, Channels = [.. channels ?? []] };
        Grants.Add(grant);
        return grant;
    }

    public OwnerRole Role(string ownerId, string name, params string[] permissions)
    {
        OwnerRole role = new() { OwnerId = ownerId, Name = name, NormalizedName = name.ToUpperInvariant(), Permissions = [.. permissions] };
        Roles.Add(role);
        return role;
    }

    public Task<IReadOnlyList<OwnerRole>> ListRolesAsync(string ownerId) =>
        Task.FromResult<IReadOnlyList<OwnerRole>>(Roles.Where(r => r.OwnerId == ownerId).OrderBy(r => r.Name).ToList());

    public Task<OwnerRole?> GetRoleAsync(string ownerId, Guid roleId) =>
        Task.FromResult(Roles.FirstOrDefault(r => r.Id == roleId && r.OwnerId == ownerId));

    public Task<bool> TryAddRoleAsync(OwnerRole role)
    {
        if (Roles.Any(r => r.OwnerId == role.OwnerId && r.NormalizedName == role.NormalizedName)) return Task.FromResult(false);
        Roles.Add(role);
        return Task.FromResult(true);
    }

    public Task<bool> TryUpdateRoleAsync(OwnerRole role) =>
        Task.FromResult(!Roles.Any(r => r.Id != role.Id && r.OwnerId == role.OwnerId && r.NormalizedName == role.NormalizedName));

    public Task DeleteRoleAsync(OwnerRole role)
    {
        Roles.Remove(role);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListRoleHoldersAsync(string ownerId, Guid roleId) =>
        Task.FromResult<IReadOnlyList<string>>(Grants.Where(g => g.OwnerId == ownerId && g.CustomRoleId == roleId).Select(g => g.UserId).Distinct().ToList());

    public Task<IReadOnlyList<WorkspaceGrant>> ListGrantsAsync(string ownerId, string userId) =>
        Task.FromResult<IReadOnlyList<WorkspaceGrant>>(Grants.Where(g => g.OwnerId == ownerId && g.UserId == userId).ToList());

    public Task<IReadOnlyList<WorkspaceGrant>> ListTeamGrantsAsync(string ownerId) =>
        Task.FromResult<IReadOnlyList<WorkspaceGrant>>(Grants.Where(g => g.OwnerId == ownerId).ToList());

    public Task<IReadOnlyList<WorkspaceGrant>> ListGrantsForUserAsync(string userId) =>
        Task.FromResult<IReadOnlyList<WorkspaceGrant>>(Grants.Where(g => g.UserId == userId).ToList());

    public Task ReplaceGrantsAsync(string ownerId, string userId, IReadOnlyList<WorkspaceGrant> grants)
    {
        Grants.RemoveAll(g => g.OwnerId == ownerId && g.UserId == userId);
        Grants.AddRange(grants);
        return Task.CompletedTask;
    }

    public Task<TeamInvite?> GetInviteAsync(string ownerId, Guid inviteId) =>
        Task.FromResult(Invites.FirstOrDefault(i => i.Id == inviteId && i.OwnerId == ownerId));

    public Task<TeamInvite?> FindInviteByTokenHashAsync(string tokenHash) =>
        Task.FromResult(Invites.FirstOrDefault(i => i.TokenHash == tokenHash));

    public Task<TeamInvite?> FindPendingInviteAsync(string ownerId, string normalizedEmail, DateTime now) =>
        Task.FromResult(Invites.FirstOrDefault(i => i.OwnerId == ownerId && i.NormalizedEmail == normalizedEmail && i.IsPending(now)));

    public Task<IReadOnlyList<TeamInvite>> ListPendingInvitesAsync(string ownerId, DateTime now) =>
        Task.FromResult<IReadOnlyList<TeamInvite>>(Invites.Where(i => i.OwnerId == ownerId && i.IsPending(now)).ToList());

    public Task AddInviteAsync(TeamInvite invite)
    {
        Invites.Add(invite);
        return Task.CompletedTask;
    }

    public Task UpdateInviteAsync(TeamInvite invite) => Task.CompletedTask;
}

/// <summary>A workspace's connections, set by the test.</summary>
public sealed class FakeConnectionDirectory : IWorkspaceConnectionDirectory
{
    public Dictionary<string, List<(Guid Id, MessagingPlatform Platform)>> ByWorkspace { get; } = [];

    public Guid Add(string workspaceId, MessagingPlatform platform)
    {
        Guid id = Guid.NewGuid();
        if (!ByWorkspace.TryGetValue(workspaceId, out List<(Guid, MessagingPlatform)>? list))
        {
            ByWorkspace[workspaceId] = list = [];
        }

        list.Add((id, platform));
        return id;
    }

    public Task<IReadOnlyList<(Guid Id, MessagingPlatform Platform)>> ListAsync(string workspaceId) =>
        Task.FromResult<IReadOnlyList<(Guid, MessagingPlatform)>>(ByWorkspace.GetValueOrDefault(workspaceId) ?? []);
}

/// <summary>Every connection hears every push: for tests about the push itself, not who may hear it.</summary>
public sealed class PassThroughAudience : IRealtimeAudience
{
    public Task<IReadOnlyList<string>> FilterAsync(string workspaceId, IReadOnlyList<string> connections, Func<WorkspaceAccess, bool> allowed) =>
        Task.FromResult(connections);
}

public static class TeamFixture
{
    /// <summary>Invite tokens signed as the API would, with a test key.</summary>
    public static InviteTokens InviteTokens() => new(Microsoft.Extensions.Options.Options.Create(new global::Parrot.Application.Auth.JwtSettings
    {
        Secret = "a-test-signing-key-that-is-long-enough-for-hs256",
        Issuer = "ParrotApi",
        Audience = "ParrotApp",
    }), TimeProvider.System);

    /// <summary>A workspace service over fakes. Pass a team to give people grants.</summary>
    public static WorkspaceService Workspaces(FakeWorkspaceRepository workspaces, IAppLogger<WorkspaceService>? logger = null,
        FakeTeamRepository? team = null, IConnectionSourceSyncService? sources = null)
    {
        team ??= new FakeTeamRepository();
        return new WorkspaceService(workspaces, team, Access(workspaces, team), sources ?? Mock.Of<IConnectionSourceSyncService>(),
            logger ?? Mock.Of<IAppLogger<WorkspaceService>>());
    }

    /// <summary>A real access service over fakes, with a cache of its own so tests never share answers.</summary>
    public static WorkspaceAccessService Access(FakeWorkspaceRepository workspaces, FakeTeamRepository team, FakeConnectionDirectory? connections = null) =>
        new(workspaces, team, connections ?? new FakeConnectionDirectory(), new MemoryCache(new MemoryCacheOptions()), new NoAccessCacheBroadcast());
}
