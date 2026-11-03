using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Application.Contracts;

/// <summary>One product as Meta reports it. Prices are expected in the tenant's minor unit (e.g. kobo for NGN).</summary>
public sealed record WhatsAppProductInput(
    string RetailerId,
    string Name,
    string? Description,
    decimal? Price,
    decimal? SalePrice,
    string? Currency,
    string? ImageUrl,
    string? Availability,
    string? Brand,
    string? Condition,
    int? Inventory,
    string? MetaProductId,
    string? MetaCatalogueId);

public sealed record WhatsAppSyncRequest(
    Guid ConnectionId,
    IReadOnlyList<WhatsAppProductInput> Products);

public sealed record WhatsAppSyncResponse(
    Guid CatalogId,
    Guid ProductTypeId,
    int Created,
    int Updated,
    int Failed,
    IReadOnlyList<string> Errors);

public sealed record WhatsAppRetireMissingRequest(
    Guid ConnectionId,
    IReadOnlyList<string> PresentSkus,
    bool PaginationCompleted,
    int ValidSkuCount,
    int FailedProductCount);

public sealed record WhatsAppRetireMissingResponse(
    Guid CatalogId,
    int Retired,
    bool Skipped,
    string? SkipReason);

public sealed record WhatsAppProductsQuery(
    Guid ConnectionId,
    string? Cursor = null,
    int? Limit = null);

public sealed record WhatsAppProductResponse(
    Guid Id,
    string RetailerId,
    string Name,
    string? Description,
    string? ImageUrl,
    string? Brand,
    string? Availability,
    string? Condition,
    decimal? Price,
    string? Currency,
    decimal? SalePrice,
    ProductStatus Status,
    DateTime? PublishedAt,
    DateTime? RetiredAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);
