namespace Parrot.Application.Contracts.WhatsAppCatalogue;

public record CatalogueItemResponse(
    Guid Id,
    string RetailerId,
    string Name,
    string? Description,
    decimal? Price,
    string? Currency,
    string? ImageUrl,
    string? Availability,
    string? Url,
    string? Brand,
    string? Condition,
    decimal? SalePrice,
    int? Inventory,
    string ChannelSource,
    DateTime LastSyncedAt,
    DateTime CreatedAt);

public record CatalogueItemsResponse(
    IReadOnlyList<CatalogueItemResponse> Items,
    int Total,
    int Page,
    int PageSize,
    string? NextCursor = null);
