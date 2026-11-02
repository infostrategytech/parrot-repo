namespace Parrot.Application.DTOs.Integrations;

public sealed record MetaCatalogueProduct
{
    public string MetaProductId { get; init; } = string.Empty;

    // The merchant's own SKU; used as the idempotency key alongside ConnectionId.
    public string RetailerId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }

    // Price in major units (e.g. 10.00 for $10.00). Meta sends minor units; the client divides by 100.
    public decimal? Price { get; init; }
    public string? Currency { get; init; }

    public string? ImageUrl { get; init; }
    public IReadOnlyList<string> AdditionalImageUrls { get; init; } = [];

    // Meta availability enum: "in stock", "out of stock", "preorder", etc.
    public string? Availability { get; init; }

    // Always "approved" when return_only_approved_products=true, kept for completeness.
    public string? ReviewStatus { get; init; }

    public string? Url { get; init; }
    public string? Brand { get; init; }
    public string? Condition { get; init; }
    public decimal? SalePrice { get; init; }
    public int? Inventory { get; init; }
}

public sealed record MetaCatalogueProductsPage(
    IReadOnlyList<MetaCatalogueProduct> Products,
    // Null when this is the last page.
    string? NextCursor);

public sealed class MetaCatalogueApiException(string message, string? errorCode = null) : Exception(message)
{
    public string? ErrorCode { get; } = errorCode;
}
