using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Domain.Entities;

public sealed class ProductType : ITenantScoped, ITimestamped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ProductTypeKind Kind { get; set; }

    // The newest version; products pin the version they were validated against.
    public int Version { get; set; } = 1;

    public ProductTypeStatus Status { get; set; } = ProductTypeStatus.Active;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public sealed class AttributeDefinition : ITenantScoped, ITimestamped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ProductTypeId { get; set; }

    public int TypeVersion { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public AttributeDataType DataType { get; set; }

    public string? Unit { get; set; }

    public bool Required { get; set; }

    // JSON array of strings; only meaningful for enum / multi_enum.
    public string? EnumOptions { get; set; }

    public bool Filterable { get; set; }

    public bool Searchable { get; set; }

    public bool Embeddable { get; set; }

    // True when this key lives on variants (ram_gb, colour) rather than the product.
    public bool VariantAxis { get; set; }

    public string? DisplayGroup { get; set; }

    public int SortOrder { get; set; }

    // JSON object: { "min": n, "max": n, "regex": "...", "max_length": n }
    public string? Validation { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
