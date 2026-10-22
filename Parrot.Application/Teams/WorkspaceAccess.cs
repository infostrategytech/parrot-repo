using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.Teams;

/// <summary>
/// What one person can do in one workspace: everything as its owner, or what
/// their grant lists. A grant limited to some channels also carries the
/// workspace's connections on those channels, so a conversation can be
/// checked by its connection id alone.
/// </summary>
public sealed record WorkspaceAccess(
    Workspace Workspace,
    string Role,
    IReadOnlySet<string> Permissions,
    IReadOnlySet<string> Channels,
    IReadOnlySet<Guid>? AllowedConnections,
    bool IsCustomRole = false)
{
    public bool IsOwner => Role == WorkspaceRoles.Owner && !IsCustomRole;

    /// <summary>True when every channel is reachable: the owner, or a grant with no channel limit.</summary>
    public bool AllChannels => AllowedConnections is null;

    public bool Can(string permission) => IsOwner || Permissions.Contains(permission);

    public bool CanReach(Guid connectionId) => AllowedConnections is null || AllowedConnections.Contains(connectionId);

    public bool CanReach(MessagingPlatform platform) => Channels.Count == 0 || Channels.Contains(WorkspaceChannels.KeyOf(platform));

    public static WorkspaceAccess ForOwner(Workspace workspace) =>
        new(workspace, WorkspaceRoles.Owner, new HashSet<string>(), new HashSet<string>(), null);

    /// <param name="roleName">The grant's role as people see it: a built-in name or the owner's own.</param>
    /// <param name="permissions">That role's permissions now.</param>
    /// <param name="connections">The workspace's connections and their platforms; only read when the grant names channels.</param>
    public static WorkspaceAccess ForGrant(
        Workspace workspace,
        WorkspaceGrant grant,
        string roleName,
        IEnumerable<string> permissions,
        IReadOnlyList<(Guid Id, MessagingPlatform Platform)> connections)
    {
        HashSet<string> channels = grant.Channels.ToHashSet(StringComparer.Ordinal);
        HashSet<Guid>? allowed = channels.Count == 0
            ? null
            : connections.Where(c => channels.Contains(WorkspaceChannels.KeyOf(c.Platform))).Select(c => c.Id).ToHashSet();

        return new(workspace, roleName, permissions.ToHashSet(StringComparer.Ordinal), channels, allowed, grant.CustomRoleId is not null);
    }
}
