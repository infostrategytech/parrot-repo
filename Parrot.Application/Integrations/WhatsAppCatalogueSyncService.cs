using Parrot.Application.Contracts.WhatsAppCatalogue;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Integrations;

public class WhatsAppCatalogueSyncService(
    IWhatsAppConnectionRepository connectionRepository,
    IWhatsAppCatalogueItemRepository itemRepository,
    IMetaCatalogueClient catalogueClient,
    IWhatsAppTokenProtector tokenProtector,
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

        if (catalogueId is null)
        {
            logger.LogInformation("whatsapp.catalogue.sync.no_catalogue ConnectionId={ConnectionId}", connectionId);
            return new CatalogueSyncResult(Added: 0, Updated: 0, Deleted: 0, Failed: 0, CatalogueId: null);
        }

        IReadOnlyList<WhatsAppCatalogueItem> existing =
            await itemRepository.GetActiveByConnectionAsync(connectionId, ct);
        Dictionary<string, WhatsAppCatalogueItem> existingByRetailerId =
            existing.ToDictionary(i => i.RetailerId);

        // Fetch all approved products from Meta before making any writes.
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

        DateTime now = DateTime.UtcNow;
        HashSet<string> seenRetailerIds = new(allProducts.Count);
        List<WhatsAppCatalogueItem> toInsert = [];
        List<WhatsAppCatalogueItem> toUpdate = [];

        foreach (MetaCatalogueProduct product in allProducts)
        {
            seenRetailerIds.Add(product.RetailerId);

            if (existingByRetailerId.TryGetValue(product.RetailerId, out WhatsAppCatalogueItem? item))
            {
                ApplyFields(item, product, catalogueId, now);
                toUpdate.Add(item);
            }
            else
            {
                toInsert.Add(CreateItem(connectionId, catalogueId, product, now));
            }
        }

        if (toInsert.Count > 0) await itemRepository.AddRangeAsync(toInsert, ct);
        if (toUpdate.Count > 0) await itemRepository.UpdateRangeAsync(toUpdate, ct);

        // Only tombstone when we received clean data. If every product had a bad retailer id,
        // skip tombstoning to avoid silently wiping the existing catalogue.
        int deleted = 0;
        if (seenRetailerIds.Count > 0 || failed == 0)
            deleted = await itemRepository.TombstoneMissingAsync(connectionId, seenRetailerIds, now, ct);

        logger.LogInformation(
            "whatsapp.catalogue.sync.completed ConnectionId={ConnectionId} CatalogueId={CatalogueId} Added={Added} Updated={Updated} Deleted={Deleted} Failed={Failed}",
            connectionId, catalogueId, toInsert.Count, toUpdate.Count, deleted, failed);

        return new CatalogueSyncResult(toInsert.Count, toUpdate.Count, deleted, failed, catalogueId);
    }

    public async Task<CatalogueItemsResponse> GetProductsAsync(
        string workspaceId, Guid connectionId, int page, int pageSize, CancellationToken ct)
    {
        WhatsAppConnection? connection = await connectionRepository.GetByOwnerAndIdAsync(workspaceId, connectionId);
        if (connection is null) throw new NotFoundException("WhatsApp connection not found.");

        int size = Math.Clamp(pageSize, 1, MaxPageSize);
        int p = Math.Max(1, page);

        (IReadOnlyList<WhatsAppCatalogueItem> items, int total) =
            await itemRepository.GetPageByConnectionAsync(connectionId, p, size, ct);

        return new CatalogueItemsResponse(
            items.Select(ToResponse).ToList(),
            total, p, size);
    }

    private static void ApplyFields(
        WhatsAppCatalogueItem item, MetaCatalogueProduct product, string catalogueId, DateTime now)
    {
        item.MetaCatalogueId = catalogueId;
        item.MetaProductId = product.MetaProductId;
        item.Name = product.Name;
        item.Description = product.Description;
        item.Price = product.Price;
        item.Currency = product.Currency;
        item.ImageUrl = product.ImageUrl;
        item.Availability = product.Availability;
        item.ReviewStatus = product.ReviewStatus;
        item.Url = product.Url;
        item.Brand = product.Brand;
        item.Condition = product.Condition;
        item.SalePrice = product.SalePrice;
        item.Inventory = product.Inventory;
        item.DeletedAt = null;
        item.LastSyncedAt = now;
        item.UpdatedAt = now;
    }

    private static WhatsAppCatalogueItem CreateItem(
        Guid connectionId, string catalogueId, MetaCatalogueProduct product, DateTime now) =>
        new()
        {
            ConnectionId = connectionId,
            MetaCatalogueId = catalogueId,
            MetaProductId = product.MetaProductId,
            RetailerId = product.RetailerId,
            Name = product.Name,
            Description = product.Description,
            Price = product.Price,
            Currency = product.Currency,
            ImageUrl = product.ImageUrl,
            Availability = product.Availability,
            ReviewStatus = product.ReviewStatus,
            Url = product.Url,
            Brand = product.Brand,
            Condition = product.Condition,
            SalePrice = product.SalePrice,
            Inventory = product.Inventory,
            LastSyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static CatalogueItemResponse ToResponse(WhatsAppCatalogueItem item) =>
        new(item.Id, item.RetailerId, item.Name, item.Description,
            item.Price, item.Currency, item.ImageUrl, item.Availability,
            item.Url, item.Brand, item.Condition, item.SalePrice, item.Inventory,
            item.ChannelSource, item.LastSyncedAt, item.CreatedAt);
}
