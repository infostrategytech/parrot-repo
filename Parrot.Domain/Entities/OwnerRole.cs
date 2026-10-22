namespace Parrot.Domain.Entities;

/// <summary>
/// A role an owner made for their own team: a name and the permissions it
/// gives. Only that owner sees or uses it. The built-in roles cannot be
/// changed; an owner who wants something close to one duplicates it into a
/// role of their own and changes that. Changing a role changes what everyone
/// holding it may do, on their next request.
/// </summary>
public class OwnerRole
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string OwnerId { get; set; }

    /// <summary>Unique among this owner's roles, ignoring case. May match a built-in role's name; custom roles are always marked as such.</summary>
    public required string Name { get; set; }

    /// <summary>Upper-cased <see cref="Name"/>, for the uniqueness check.</summary>
    public required string NormalizedName { get; set; }

    /// <summary>Permission keys, e.g. "conversations.reply".</summary>
    public List<string> Permissions { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
