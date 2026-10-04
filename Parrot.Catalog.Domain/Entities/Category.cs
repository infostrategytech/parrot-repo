namespace Parrot.Catalog.Domain.Entities;

public sealed class Category : ITenantScoped, ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    public Guid? ParentId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>One row per (ancestor, descendant) pair including depth 0 (self).</summary>
public sealed class CategoryClosure : ITenantScoped
{
    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    public Guid AncestorId { get; set; }

    public Guid DescendantId { get; set; }

    public int Depth { get; set; }
}
