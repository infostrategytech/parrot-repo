using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Domain.Entities;

public sealed class Offer : ITenantScoped, ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    // Exactly one of the two is set (CHECK constraint).
    public Guid? ProductId { get; set; }

    public Guid? VariantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public BillingPeriod BillingPeriod { get; set; } = BillingPeriod.OneTime;

    public OfferStatus Status { get; set; } = OfferStatus.Draft;

    // Rules evaluated in Phase 4; stored and returned verbatim until then.
    public string Eligibility { get; set; } = "{}";

    public DateTime ValidFrom { get; set; }

    public DateTime? ValidUntil { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

public sealed class Price : ITenantScoped, ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    public Guid OfferId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "USD";

    public PriceType PriceType { get; set; } = PriceType.List;

    public PriceStatus Status { get; set; } = PriceStatus.Active;

    public DateTime ValidFrom { get; set; }

    public DateTime? ValidUntil { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

public sealed class Availability : ITenantScoped, ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    public Guid? ProductId { get; set; }

    public Guid? VariantId { get; set; }

    // ISO 3166-1 alpha-2, or "*" for everywhere.
    public string Region { get; set; } = "*";

    public int? StockQty { get; set; }

    public int? Capacity { get; set; }

    public AvailabilityStatus Status { get; set; } = AvailabilityStatus.Active;

    public DateTime ValidFrom { get; set; }

    public DateTime? ValidUntil { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

public sealed class ProductRelationship : ITenantScoped, ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    public Guid SourceId { get; set; }

    public Guid TargetId { get; set; }

    public RelationshipType RelationshipType { get; set; }

    public string Metadata { get; set; } = "{}";

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

public sealed class MediaAsset : ITenantScoped, ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CatalogId { get; set; }

    public Guid ProductId { get; set; }

    public Guid? VariantId { get; set; }

    public MediaKind Kind { get; set; } = MediaKind.Image;

    // Object key only; the bucket is configuration. Prefixed by tenant id.
    public string S3Key { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    public string? AltText { get; set; }

    public string? Title { get; set; }

    public long? SizeBytes { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}
