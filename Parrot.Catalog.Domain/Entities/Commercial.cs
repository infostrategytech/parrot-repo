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

    // What one price buys, such as "person" or "hour"; null when it is simply the item.
    public string? Unit { get; set; }

    // The business quotes the price when asked; such an offer carries no prices.
    public bool PriceOnRequest { get; set; }

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

    public AvailabilityMode Mode { get; set; } = AvailabilityMode.InStock;

    // Days needed to make it; only for made_to_order.
    public int? LeadTimeDays { get; set; }

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

    /// <summary>
    /// What the AI made of this photo in its latest report that named it: embedded (it learned
    /// it), rejected or failed. Null until a report names it. Only embedded photos are offered
    /// to the AI's agents to send to customers.
    /// </summary>
    public string? AiStatus { get; set; }

    /// <summary>When the report that set <see cref="AiStatus"/> was made.</summary>
    public DateTime? AiStatusAt { get; set; }

    /// <summary>
    /// The link the AI says it learned this photo from, as its report gave it. The AI's tools
    /// return this link, so it matches what the AI holds; null when the report gave none.
    /// </summary>
    public string? AiUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>The AI's per-photo statuses, as its ingestion report names them.</summary>
public static class MediaAiStatus
{
    public const string Embedded = "embedded";

    public const int MaxLength = 32;

    public const int MaxUrlLength = 2048;
}
