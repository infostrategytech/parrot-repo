using Parrot.Application.Contracts.WhatsAppCatalogue;
using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

/// <summary>
/// Calls the Parrot.Catalog service on behalf of one workspace to sync
/// WhatsApp catalogue data. Implementations use a short-lived service token
/// with <c>catalog:admin</c> scope.
/// </summary>
public interface ICatalogImportClient
{
    /// <summary>
    /// Bulk-upserts products from Meta into the catalog.
    /// Returns insert/update/fail counts for the sync result.
    /// </summary>
    Task<CatalogImportSyncResult> SyncProductsAsync(
        string workspaceId,
        Guid connectionId,
        IReadOnlyList<MetaCatalogueProduct> products,
        CancellationToken ct);

    /// <summary>
    /// Retires catalog products whose SKUs were absent from Meta's latest full result.
    /// Returns the number of products retired.
    /// </summary>
    Task<int> RetireMissingAsync(
        string workspaceId,
        Guid connectionId,
        IReadOnlyList<string> presentSkus,
        bool paginationCompleted,
        int validSkuCount,
        int failedProductCount,
        CancellationToken ct);

    /// <summary>Paginated product read from the catalog.</summary>
    Task<CatalogueItemsResponse> GetProductsAsync(
        string workspaceId,
        Guid connectionId,
        int page,
        int pageSize,
        string? cursor,
        CancellationToken ct);
}

/// <summary>Counts returned by a catalog sync call.</summary>
public sealed record CatalogImportSyncResult(int Created, int Updated, int Failed);
