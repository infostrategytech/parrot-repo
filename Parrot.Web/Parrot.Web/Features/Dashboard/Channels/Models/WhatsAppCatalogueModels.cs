namespace Parrot.Web.Features.Dashboard.Channels.Models;

public sealed record CatalogueSyncResult(int Added, int Updated, int Deleted, int Failed, string? CatalogueId);

public sealed record CatalogueItem(
    Guid Id,
    string RetailerId,
    string Name,
    string? Description,
    decimal? Price,
    string? Currency,
    string? ImageUrl,
    string? Availability,
    string? Brand,
    DateTime LastSyncedAt);

public sealed record CatalogueItemsResponse(
    List<CatalogueItem> Items,
    int Total,
    int Page,
    int PageSize,
    string? NextCursor = null);
