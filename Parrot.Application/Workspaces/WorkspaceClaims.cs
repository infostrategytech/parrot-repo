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
}
