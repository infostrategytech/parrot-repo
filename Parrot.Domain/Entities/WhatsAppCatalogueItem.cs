namespace Parrot.Domain.Entities;

public class WhatsAppCatalogueItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConnectionId { get; set; }

    // Identifies the source channel for filtering. Always "whatsapp" for items from this table.
    public string ChannelSource { get; set; } = "whatsapp";

    // The Meta catalogue this item belongs to (from WABA → product_catalogs).
    public required string MetaCatalogueId { get; set; }

    // Meta's internal product id.
    public required string MetaProductId { get; set; }

    // The merchant's own SKU. Used as the idempotency key with ConnectionId.
    public required string RetailerId { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    // Price in major units (e.g. 25.00 for ₦25).
    public decimal? Price { get; set; }

    // ISO 4217 currency code (e.g. "NGN").
    public string? Currency { get; set; }

    public string? ImageUrl { get; set; }

    // Meta availability enum: "in stock", "out of stock", "preorder", etc.
    public string? Availability { get; set; }

    public string? ReviewStatus { get; set; }
    public string? Url { get; set; }
    public string? Brand { get; set; }
    public string? Condition { get; set; }
    public decimal? SalePrice { get; set; }
    public int? Inventory { get; set; }

    // Nullable forward-reference to a Parrot Catalog product when this item has been linked.
    // No FK: Parrot.Catalog uses a separate DbContext and Postgres schema.
    public Guid? CatalogProductId { get; set; }

    public DateTime LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Soft-delete: set when the item is absent from the latest approved sync.
    public DateTime? DeletedAt { get; set; }
}
