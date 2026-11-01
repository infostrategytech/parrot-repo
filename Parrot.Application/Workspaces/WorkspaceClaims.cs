namespace Parrot.Application.Workspaces;

/// <summary>How a session token says which workspace it is working in.</summary>
public static class WorkspaceClaims
{
    /// <summary>
    /// The active workspace's id. A token issued before workspaces existed has
    /// none; its user id stands in, which is that tenant's first workspace.
    /// </summary>
    public const string WorkspaceId = "workspace_id";

    /// <summary>The active workspace's name. Left out while the workspace has none.</summary>
    public const string WorkspaceName = "workspace_name";

    /// <summary>"Owner", or the role the person holds in the workspace.</summary>
    public const string WorkspaceRole = "workspace_role";

    /// <summary>
    /// What the person may do, as permission codes ("cv cr"), or "*" for the
    /// owner. A copy for clients to read: the API checks the person's grant on
    /// every request, so a change takes effect before the token is replaced.
    /// See WorkspacePermissions.Abbreviate and Expand.
    /// </summary>
    public const string Permissions = "perms";

    /// <summary>The channels the person reaches, as codes ("wa ig"), or "*" for all. See WorkspaceChannels.</summary>
    public const string Channels = "chans";
}
