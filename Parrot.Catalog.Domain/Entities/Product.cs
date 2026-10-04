using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Domain.Entities;

public sealed class Product : ITenantScoped, ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    public Guid ProductTypeId { get; set; }

    public int TypeVersion { get; set; }

    public Guid? CategoryId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ProductStatus Status { get; set; } = ProductStatus.Draft;

    // Validated JSON object; keys are registry keys for the pinned type version.
    public string Attributes { get; set; } = "{}";

    // Derived: name, sku, description and searchable attribute values (ADR-002).
    public string SearchText { get; set; } = string.Empty;

    // Incremented on every product_versions snapshot.
    public int Version { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? RetiredAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public string UpdatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

public sealed class ProductVariant : ITenantScoped, ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    public Guid ProductId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    // Variant-axis attribute values only.
    public string Attributes { get; set; } = "{}";

    public VariantStatus Status { get; set; } = VariantStatus.Active;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>Derived, never truth. Rebuilt in the writing transaction (ADR-002).</summary>
public sealed class ProductAttributeIndex : ITenantScoped
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    public Guid ProductId { get; set; }

    public Guid? VariantId { get; set; }

    public string AttributeKey { get; set; } = string.Empty;

    public decimal? NumericValue { get; set; }

    public string? TextValue { get; set; }

    public bool? BoolValue { get; set; }

    public DateTime? DateValue { get; set; }
}

public sealed class ProductVersion : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    public Guid ProductId { get; set; }

    public int VersionNumber { get; set; }

    public string Snapshot { get; set; } = "{}";

    public string ContentHash { get; set; } = string.Empty;

    public string PublishedBy { get; set; } = string.Empty;

    public DateTime PublishedAt { get; set; }
}

public sealed class CatalogDocument : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    public Guid ProductId { get; set; }

    public Guid? VariantId { get; set; }

    public string RenderedText { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    // Phase 2 adds: embedding vector(N), embedded_at.
    public DateTime UpdatedAt { get; set; }
}
