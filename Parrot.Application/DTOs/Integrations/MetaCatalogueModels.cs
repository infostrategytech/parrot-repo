namespace Parrot.Application.DTOs.Integrations;

public sealed record MetaCatalogueProduct
{
    public string MetaProductId { get; init; } = string.Empty;

    // The merchant's own SKU; used as the idempotency key alongside ConnectionId.
    public string RetailerId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }

    // The original price string exactly as returned by Meta (e.g. "1000 USD").
    // No parsing or unit conversion is applied.
    public string? RawPrice { get; init; }
    public string? RawSalePrice { get; init; }

    // The ISO 4217 code from the separate "currency" JSON field.
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
