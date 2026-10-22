namespace Parrot.Domain.Entities;

/// <summary>One grant an invite will create once accepted. See <see cref="WorkspaceGrant"/>.</summary>
public sealed record GrantSpec
{
    /// <summary>The workspace, or null for all of the owner's workspaces.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>A built-in role by id; null when <see cref="CustomRoleId"/> is set.</summary>
    public string? SystemRole { get; init; }

    /// <summary>One of the owner's own roles; null for a built-in one.</summary>
    public Guid? CustomRoleId { get; init; }

    /// <summary>Platform names; empty is every channel.</summary>
    public List<string> Channels { get; init; } = [];
}

/// <summary>
/// An invitation to join an owner's team, sent by email. The link carries a
/// signed invite token (see InviteTokens); only its SHA-256 hash is kept, so a
/// copy of this table cannot be used to join anything. It is pending until it is accepted,
/// revoked or past <see cref="ExpiresAt"/>.
/// </summary>
public class TeamInvite
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant whose team it joins.</summary>
    public required string OwnerId { get; set; }

    /// <summary>The address as typed, for display and for sending.</summary>
    public required string Email { get; set; }

    /// <summary>Upper-cased, the way Identity normalizes emails, so one person cannot be invited twice by case.</summary>
    public required string NormalizedEmail { get; set; }

    public required string FullName { get; set; }

    /// <summary>What the person gets on accepting: one grant per workspace, or one for all.</summary>
    public List<GrantSpec> Grants { get; set; } = [];

    /// <summary>Hex SHA-256 of the token in the link.</summary>
    public required string TokenHash { get; set; }

    public required string InvitedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public string? AcceptedByUserId { get; set; }

    public DateTime? RevokedAt { get; set; }

    public bool IsPending(DateTime now) => AcceptedAt is null && RevokedAt is null && ExpiresAt > now;
}
