namespace Parrot.Domain.Entities;

/// <summary>
/// What one person may do in an owner's workspaces. The team belongs to the
/// owner (the tenant), not to a workspace: a person is in the team while they
/// hold at least one grant. A grant is for one workspace, or, with no
/// <see cref="WorkspaceId"/>, for every workspace the owner has or makes
/// later. In a workspace that has its own grant, that grant is used instead
/// of the every-workspace one, so access can be narrowed in one place.
///
/// The owner has no grants: owning a workspace grants everything in it.
/// </summary>
public class WorkspaceGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant whose team this is: the owner of the workspaces it covers.</summary>
    public required string OwnerId { get; set; }

    public required string UserId { get; set; }

    /// <summary>The workspace it is for, or null for all of the owner's workspaces.</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// A built-in role by id, e.g. "analyst", or null when the grant uses one of
    /// the owner's custom roles. Exactly one of this and <see cref="CustomRoleId"/>
    /// is set. What the grant allows is the role's permissions, read when access
    /// is checked, so changing a role changes every grant that uses it.
    /// </summary>
    public string? SystemRole { get; set; }

    /// <summary>One of the owner's own roles (<see cref="OwnerRole"/>), or null for a built-in one.</summary>
    public Guid? CustomRoleId { get; set; }

    /// <summary>
    /// The channels it reaches, by platform name ("whatsapp", "instagram",
    /// "facebook", "tiktok"). Empty is every channel, including platforms
    /// connected later.
    /// </summary>
    public List<string> Channels { get; set; } = [];

    public string? InvitedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
