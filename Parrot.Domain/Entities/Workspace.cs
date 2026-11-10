namespace Parrot.Domain.Entities;

/// <summary>
/// One business a tenant runs. Everything a customer sees — agents, channels,
/// conversations, the catalog — belongs to a workspace, and the workspace id
/// is what those rows are keyed by. Its owner is the user who created it
/// (the tenant); one owner can run several.
/// </summary>
public class Workspace
{
    /// <summary>
    /// A string, because it stands where a user id used to: every owner column
    /// and event was keyed by the user. Accounts made before workspaces had ids
    /// of their own kept their user id as their first workspace's id, so rows
    /// written before workspaces existed still belong to it. New accounts get a
    /// first workspace with its own id (WorkspaceService.FirstWorkspaceId), so
    /// no code may assume a workspace id equals its owner's user id.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The user who created it and owns it: the tenant.</summary>
    public required string OwnerId { get; set; }

    /// <summary>The business name. Null until setup has saved one.</summary>
    public string? Name { get; set; }

    public string? LogoUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
