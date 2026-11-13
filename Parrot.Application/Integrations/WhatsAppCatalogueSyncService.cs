using Parrot.Application.BusinessSources;
using Parrot.Application.Contracts.WhatsAppCatalogue;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Integrations;

public class WhatsAppCatalogueSyncService(
    IWhatsAppConnectionRepository connectionRepository,
    ICatalogImportClient catalogImportClient,
    IMetaCatalogueClient catalogueClient,
    IWhatsAppTokenProtector tokenProtector,
    IConnectionSourceSyncService sourceSyncService,
    IAppLogger<WhatsAppCatalogueSyncService> logger) : IWhatsAppCatalogueSyncService
{
    private const int MaxPageSize = 100;

    public async Task<CatalogueSyncResult> SyncAsync(string workspaceId, Guid connectionId, CancellationToken ct)
    {
        WhatsAppConnection connection = await connectionRepository.GetByOwnerAndIdAsync(workspaceId, connectionId)
            ?? throw new NotFoundException("WhatsApp connection not found.");

        if (connection.DisabledAt.HasValue)
            throw new ValidationException("This WhatsApp connection is disabled.");

        if (string.IsNullOrEmpty(connection.WhatsAppBusinessAccountId))
            throw new ValidationException("This connection does not have a WhatsApp Business Account configured.");

        if (string.IsNullOrEmpty(connection.AccessTokenEncrypted))
            throw new ValidationException("This connection does not have an access token configured.");

        string accessToken = tokenProtector.Unprotect(connection.AccessTokenEncrypted);

        string? catalogueId = await catalogueClient.GetCatalogueIdAsync(
            connection.WhatsAppBusinessAccountId, accessToken, ct);

        // Best-effort: keep the AI's view of this workspace in sync with the catalog state.
        // A failure here does not abort the product sync; the reconciliation job will catch up.
        try
        {
            await sourceSyncService.SyncWhatsAppCatalogAsync(workspaceId, connectionId, catalogueId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "whatsapp.catalog.source.sync_failed WorkspaceId={WorkspaceId} ConnectionId={ConnectionId} CatalogueId={CatalogueId}",
                workspaceId, connectionId, catalogueId);
        }

        if (catalogueId is null)
        {
            logger.LogInformation("whatsapp.catalogue.sync.no_catalogue ConnectionId={ConnectionId}", connectionId);
            return new CatalogueSyncResult(Added: 0, Updated: 0, Deleted: 0, Failed: 0, CatalogueId: null);
        }

        List<MetaCatalogueProduct> allProducts = [];
        int failed = 0;
        string? cursor = null;
        do
        {
            MetaCatalogueProductsPage page =
                await catalogueClient.GetApprovedProductsAsync(catalogueId, accessToken, cursor, ct);

            foreach (MetaCatalogueProduct product in page.Products)
            {
                if (string.IsNullOrWhiteSpace(product.RetailerId))
                {
                    logger.LogWarning(
                        "whatsapp.catalogue.sync.skipped_no_retailer_id ConnectionId={ConnectionId} MetaProductId={MetaProductId}",
                        connectionId, product.MetaProductId);
                    failed++;
                    continue;
                }
                allProducts.Add(product);
            }
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        CatalogImportSyncResult syncResult = await catalogImportClient.SyncProductsAsync(
            workspaceId, connectionId, allProducts, ct);

        HashSet<string> presentSkus = new(allProducts.Select(p => p.RetailerId));
        int retired = 0;
        // Only retire when we received clean data: if every product had a bad
        // retailer id, skip retiring to avoid silently wiping the existing catalogue.
        if (presentSkus.Count > 0 || failed == 0)
        {
            retired = await catalogImportClient.RetireMissingAsync(
                workspaceId, connectionId,
                presentSkus.ToList(),
                paginationCompleted: true,
                validSkuCount: allProducts.Count,
                failedProductCount: failed,
                ct);
        }

        logger.LogInformation(
            "whatsapp.catalogue.sync.completed ConnectionId={ConnectionId} CatalogueId={CatalogueId} Added={Added} Updated={Updated} Deleted={Deleted} Failed={Failed}",
            connectionId, catalogueId, syncResult.Created, syncResult.Updated, retired, syncResult.Failed + failed);

        return new CatalogueSyncResult(syncResult.Created, syncResult.Updated, retired, syncResult.Failed + failed, catalogueId);
    }

    public async Task<CatalogueItemsResponse> GetProductsAsync(
        string workspaceId, Guid connectionId, int page, int pageSize, string? cursor, CancellationToken ct)
    {
        WhatsAppConnection? connection = await connectionRepository.GetByOwnerAndIdAsync(workspaceId, connectionId);
        if (connection is null) throw new NotFoundException("WhatsApp connection not found.");

        int size = Math.Clamp(pageSize, 1, MaxPageSize);
        int p = Math.Max(1, page);

        return await catalogImportClient.GetProductsAsync(workspaceId, connectionId, p, size, cursor, ct);
    }
}
