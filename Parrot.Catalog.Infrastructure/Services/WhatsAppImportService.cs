using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Parrot.Catalog.Application.Audit;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Events;
using Parrot.Catalog.Application.Pagination;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

public sealed class WhatsAppImportService : AdminServiceBase, IWhatsAppImportService
{
    private const string CatalogKeyPrefix = "whatsapp_";
    private const string ProductTypeKey = "whatsapp_product";
    private const string ProductTypeName = "WhatsApp Import";
    private const string GlobalRegion = "*";

    private static readonly Dictionary<string, string> AvailabilityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["in stock"] = "in_stock",
        ["in_stock"] = "in_stock",
        ["out of stock"] = "out_of_stock",
        ["out_of_stock"] = "out_of_stock",
        ["preorder"] = "preorder",
        ["available for order"] = "available_for_order",
        ["available_for_order"] = "available_for_order",
        ["discontinued"] = "discontinued",
    };

    private readonly ICatalogAdminService _catalogs;
    private readonly IProductTypeAdminService _productTypes;
    private readonly IWriteSchemaRegistry _registry;
    private readonly DerivedDataRebuilder _rebuilder;
    private readonly ILogger<WhatsAppImportService> _logger;

    public WhatsAppImportService(
        CatalogDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        CatalogMetrics metrics,
        ItemEventWriter itemEvents,
        ICatalogAdminService catalogs,
        IProductTypeAdminService productTypes,
        IWriteSchemaRegistry registry,
        DerivedDataRebuilder rebuilder,
        ILogger<WhatsAppImportService> logger)
        : base(db, tenant, clock, metrics, itemEvents)
    {
        _catalogs = catalogs;
        _productTypes = productTypes;
        _registry = registry;
        _rebuilder = rebuilder;
        _logger = logger;
    }

    public Task<WhatsAppSyncResponse> SyncAsync(WhatsAppSyncRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        if (request.ConnectionId == Guid.Empty)
        {
            throw new ValidationException("connection_id is required.");
        }

        if (request.Products is null || request.Products.Count == 0)
        {
            // Auto-provision catalog/type even on an empty batch so IDs are returned
            CatalogResponse catalogResp = await EnsureCatalogAsync(request.ConnectionId, c);
            ProductTypeResponse typeResp = await EnsureProductTypeAsync(c);
            return new WhatsAppSyncResponse(catalogResp.Id, typeResp.Id, 0, 0, 0, []);
        }

        CatalogResponse catalog = await EnsureCatalogAsync(request.ConnectionId, c);
        ProductTypeResponse productType = await EnsureProductTypeAsync(c);
        TypeSchema schema = await _registry.GetAsync(productType.Id, productType.Version, c);

        // Load everything for this catalog+type in bulk
        Dictionary<string, Product> existingBySku = await Db.Products
            .Where(p => p.CatalogId == catalog.Id && p.ProductTypeId == productType.Id && p.DeletedAt == null)
            .ToDictionaryAsync(p => p.Sku, StringComparer.Ordinal, c);

        List<Guid> existingIds = [.. existingBySku.Values.Select(p => p.Id)];

        Dictionary<Guid, Availability> availByProductId = existingIds.Count == 0
            ? []
            : await Db.Availability
                .Where(a => a.ProductId != null && existingIds.Contains(a.ProductId.Value) && a.Region == GlobalRegion && a.DeletedAt == null)
                .ToDictionaryAsync(a => a.ProductId!.Value, c);

        int created = 0, updated = 0, failed = 0;
        List<string> errors = [];
        List<(Product Product, bool IsNew)> processed = [];

        foreach (WhatsAppProductInput input in request.Products)
        {
            try
            {
                (Product product, bool isNew) = ProcessProduct(
                    input, catalog.Id, productType, schema,
                    existingBySku, availByProductId);
                processed.Add((product, isNew));
                if (isNew) created++; else updated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                string safeRetailerId = string.IsNullOrEmpty(input.RetailerId) ? "(empty)" : input.RetailerId;
                errors.Add($"SKU {safeRetailerId}: {ex.Message}");
                _logger.LogWarning(ex, "WhatsApp import: skipping product RetailerId={RetailerId} TenantId={TenantId}",
                    input.RetailerId, TenantId);
            }
        }

        // Flush products, offers, prices and availability before calling rebuilder
        await SaveAsync(c);

        foreach ((Product product, bool isNew) in processed)
        {
            product.Status = ProductStatus.Published;
            product.PublishedAt = Now;
            product.UpdatedBy = Tenant.Actor;
            Stamp(product, created: false);
            await _rebuilder.SnapshotAsync(product, schema, Tenant.Actor, c);
            await _rebuilder.RebuildAsync(product, schema, c);
            string eventType = isNew ? CatalogEventTypes.ProductPublished : CatalogEventTypes.ProductUpdated;
            Enqueue(AggregateTypes.Product, product.Id, eventType, BuildEventPayload(product));
        }

        Audit(AuditActions.Create, "whatsapp_sync", Guid.Empty, null,
            new { connection_id = request.ConnectionId, catalog_id = catalog.Id, created, updated, failed });
        return new WhatsAppSyncResponse(catalog.Id, productType.Id, created, updated, failed, errors);
    }, ct);

    public Task<WhatsAppRetireMissingResponse> RetireMissingAsync(WhatsAppRetireMissingRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        if (request.ConnectionId == Guid.Empty)
        {
            throw new ValidationException("connection_id is required.");
        }

        if (!request.PaginationCompleted)
        {
            return new WhatsAppRetireMissingResponse(Guid.Empty, 0, Skipped: true, "Pagination did not complete; retirement skipped to avoid false retirements.");
        }

        if (request.ValidSkuCount == 0 && request.FailedProductCount == 0)
        {
            return new WhatsAppRetireMissingResponse(Guid.Empty, 0, Skipped: true, "Meta returned zero products; retirement skipped to avoid wiping the catalogue.");
        }

        string catalogKey = CatalogKeyPrefix + request.ConnectionId.ToString("N");
        CatalogEntity? catalog = await Db.Catalogs.AsNoTracking()
            .FirstOrDefaultAsync(c2 => c2.Key == catalogKey && c2.DeletedAt == null, c);

        if (catalog is null)
        {
            return new WhatsAppRetireMissingResponse(Guid.Empty, 0, Skipped: true, "No catalogue has been synced yet for this connection.");
        }

        ProductType? productType = await Db.ProductTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Key == ProductTypeKey, c);

        if (productType is null)
        {
            return new WhatsAppRetireMissingResponse(catalog.Id, 0, Skipped: true, "Product type not yet provisioned.");
        }

        HashSet<string> presentSkus = new(request.PresentSkus, StringComparer.Ordinal);

        List<Product> toRetire = await Db.Products
            .Where(p => p.CatalogId == catalog.Id && p.ProductTypeId == productType.Id
                     && p.Status == ProductStatus.Published && p.DeletedAt == null)
            .ToListAsync(c);

        toRetire = toRetire.Where(p => !presentSkus.Contains(p.Sku)).ToList();

        if (toRetire.Count == 0)
        {
            return new WhatsAppRetireMissingResponse(catalog.Id, 0, Skipped: false, null);
        }

        DateTime now = Now;
        foreach (Product product in toRetire)
        {
            product.Status = ProductStatus.Retired;
            product.RetiredAt = now;
            product.UpdatedBy = Tenant.Actor;
            product.UpdatedAt = now;
            Enqueue(AggregateTypes.Product, product.Id, CatalogEventTypes.ProductRetired, BuildEventPayload(product, "retire_missing"));
        }

        Audit(AuditActions.Retire, "whatsapp_retire_missing", catalog.Id, null,
            new { connection_id = request.ConnectionId, catalog_id = catalog.Id, retired = toRetire.Count });
        return new WhatsAppRetireMissingResponse(catalog.Id, toRetire.Count, Skipped: false, null);
    }, ct);

    public async Task<PagedResponse<WhatsAppProductResponse>> GetProductsAsync(WhatsAppProductsQuery query, CancellationToken ct)
    {
        if (query.ConnectionId == Guid.Empty)
        {
            throw new ValidationException("connection_id is required.");
        }

        string catalogKey = CatalogKeyPrefix + query.ConnectionId.ToString("N");
        CatalogEntity? catalog = await Db.Catalogs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == catalogKey && c.DeletedAt == null, ct);

        if (catalog is null)
        {
            return new PagedResponse<WhatsAppProductResponse>([], null);
        }

        ProductType? productType = await Db.ProductTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Key == ProductTypeKey, ct);

        if (productType is null)
        {
            return new PagedResponse<WhatsAppProductResponse>([], null);
        }

        int limit = PageSize.Clamp(query.Limit, Limits.MaxPageSize, Limits.DefaultPageSize);
        Cursor? cursor = Cursor.Decode(query.Cursor, "created_at");

        IQueryable<Product> q = Db.Products.AsNoTracking()
            .Where(p => p.CatalogId == catalog.Id && p.ProductTypeId == productType.Id && p.DeletedAt == null);

        if (cursor is not null && long.TryParse(cursor.Value, out long ticks))
        {
            DateTime after = new DateTime(ticks, DateTimeKind.Utc);
            q = q.Where(p => p.CreatedAt < after || (p.CreatedAt == after && p.Id.CompareTo(cursor.Id) < 0));
        }

        List<Product> rows = await q
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        bool more = rows.Count > limit;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        List<WhatsAppProductResponse> items = rows.Select(p =>
        {
            // Extract availability and price strings from stored attributes.
            // avail.Mode alone can't reconstruct "out_of_stock" vs "in_stock" since both use InStock mode.
            (string? availability, string? brand, string? condition, string? imageUrl,
                string? rawPrice, string? rawSalePrice) = ExtractAttributes(p.Attributes);

            return new WhatsAppProductResponse(
                Id: p.Id,
                RetailerId: p.Sku,
                Name: p.Name,
                Description: p.Description,
                ImageUrl: imageUrl,
                Brand: brand,
                Availability: availability,
                Condition: condition,
                RawPrice: rawPrice,
                RawSalePrice: rawSalePrice,
                Status: p.Status,
                PublishedAt: p.PublishedAt,
                RetiredAt: p.RetiredAt,
                CreatedAt: p.CreatedAt,
                UpdatedAt: p.UpdatedAt);
        }).ToList();

        string? next = more
            ? new Cursor("created_at", rows[^1].CreatedAt.Ticks.ToString(), rows[^1].Id).Encode()
            : null;
        return new PagedResponse<WhatsAppProductResponse>(items, next);
    }

    // ---------- provisioning ----------

    private async Task<CatalogResponse> EnsureCatalogAsync(Guid connectionId, CancellationToken ct)
    {
        string key = CatalogKeyPrefix + connectionId.ToString("N");
        CatalogEntity? existing = await Db.Catalogs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key && c.DeletedAt == null, ct);

        if (existing is not null)
        {
            return new CatalogResponse(existing.Id, existing.Key, existing.Name, existing.DefaultCurrency,
                existing.Status, existing.CreatedAt, existing.UpdatedAt);
        }

        return await _catalogs.CreateAsync(
            new CreateCatalogRequest(key, $"WhatsApp Catalogue ({connectionId:D})", "NGN"), ct);
    }

    private async Task<ProductTypeResponse> EnsureProductTypeAsync(CancellationToken ct)
    {
        ProductType? existing = await Db.ProductTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Key == ProductTypeKey, ct);

        if (existing is not null)
        {
            return new ProductTypeResponse(existing.Id, existing.Key, existing.Name, existing.Kind,
                existing.Version, existing.Status, [], existing.CreatedAt, existing.UpdatedAt);
        }

        return await _productTypes.CreateAsync(new CreateProductTypeRequest(
            Key: ProductTypeKey,
            Name: ProductTypeName,
            Kind: ProductTypeKind.Good,
            Attributes:
            [
                new AttributeDefinitionInput("availability", "Availability", AttributeDataType.Enum,
                    Required: false, EnumOptions: ["in_stock", "out_of_stock", "preorder", "available_for_order", "discontinued"]),
                new AttributeDefinitionInput("brand", "Brand", AttributeDataType.String, Required: false),
                new AttributeDefinitionInput("condition", "Condition", AttributeDataType.Enum,
                    Required: false, EnumOptions: ["new", "refurbished", "used"]),
                new AttributeDefinitionInput("image_url", "Image URL", AttributeDataType.String, Required: false),
                new AttributeDefinitionInput("meta_product_id", "Meta Product ID", AttributeDataType.String, Required: false),
                new AttributeDefinitionInput("meta_catalogue_id", "Meta Catalogue ID", AttributeDataType.String, Required: false),
                new AttributeDefinitionInput("meta_price_raw", "Meta Price", AttributeDataType.String, Required: false),
                new AttributeDefinitionInput("meta_sale_price_raw", "Meta Sale Price", AttributeDataType.String, Required: false),
            ]), ct);
    }

    // ---------- per-product upsert ----------

    private (Product, bool) ProcessProduct(
        WhatsAppProductInput input,
        Guid catalogId,
        ProductTypeResponse productType,
        TypeSchema schema,
        Dictionary<string, Product> existingBySku,
        Dictionary<Guid, Availability> availByProductId)
    {
        string sku = Guards.RequireMatch(
            input.RetailerId?.Trim() ?? string.Empty,
            Guards.Sku(), "retailer_id", "must be 1-64 letters, digits, dots, underscores or hyphens.");

        string name = Guards.RequireText(
            (input.Name?.Length > Limits.MaxNameLength ? input.Name[..Limits.MaxNameLength] : input.Name) ?? string.Empty,
            "name", Limits.MaxNameLength);

        string? normalizedAvailability = NormalizeAvailability(input.Availability);
        string? normalizedCondition = NormalizeCondition(input.Condition);

        string attrsJson = BuildAttributes(normalizedAvailability, input.Brand, normalizedCondition,
            input.ImageUrl, input.MetaProductId, input.MetaCatalogueId, input.RawPrice, input.RawSalePrice);

        IReadOnlyList<string> validationErrors = schema.Validate(attrsJson, AttributeScope.Product);
        if (validationErrors.Count > 0)
        {
            throw new ValidationException(validationErrors);
        }

        bool isNew = !existingBySku.TryGetValue(sku, out Product? product);

        if (isNew)
        {
            product = new Product
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CatalogId = catalogId,
                ProductTypeId = productType.Id,
                TypeVersion = productType.Version,
                Sku = sku,
                Name = name,
                Slug = Guards.Slugify(name),
                Description = Guards.OptionalText(input.Description, "description", Limits.MaxDescriptionLength),
                Status = ProductStatus.Draft,
                Attributes = attrsJson,
                SearchText = name,
                CreatedBy = Tenant.Actor,
                UpdatedBy = Tenant.Actor,
            };
            Stamp(product, created: true);
            Db.Products.Add(product);
            existingBySku[sku] = product;
        }
        else
        {
            product!.Name = name;
            product.Description = Guards.OptionalText(input.Description, "description", Limits.MaxDescriptionLength);
            product.Attributes = attrsJson;
            product.UpdatedBy = Tenant.Actor;
            if (product.Status == ProductStatus.Retired)
            {
                product.Status = ProductStatus.Draft;
                product.RetiredAt = null;
            }
            Stamp(product, created: false);
        }

        UpsertAvailability(product, catalogId, normalizedAvailability, input.Inventory, availByProductId);

        return (product, isNew);
    }

    private void UpsertAvailability(
        Product product, Guid catalogId, string? normalizedAvailability, int? inventory,
        Dictionary<Guid, Availability> availByProductId)
    {
        AvailabilityMode mode = normalizedAvailability switch
        {
            "preorder" or "available_for_order" => AvailabilityMode.MadeToOrder,
            _ => AvailabilityMode.InStock,
        };

        AvailabilityStatus status = normalizedAvailability is "out_of_stock" or "discontinued"
            ? AvailabilityStatus.Inactive
            : AvailabilityStatus.Active;

        if (availByProductId.TryGetValue(product.Id, out Availability? existing))
        {
            existing.Mode = mode;
            existing.Status = status;
            existing.StockQty = inventory;
            Stamp(existing, created: false);
            return;
        }

        Availability avail = new Availability
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            CatalogId = catalogId,
            ProductId = product.Id,
            Region = GlobalRegion,
            Mode = mode,
            Status = status,
            StockQty = inventory,
            ValidFrom = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        Stamp(avail, created: true);
        Db.Availability.Add(avail);
        availByProductId[product.Id] = avail;
    }

    // ---------- helpers ----------

    private static string BuildAttributes(
        string? availability, string? brand, string? condition,
        string? imageUrl, string? metaProductId, string? metaCatalogueId,
        string? rawPrice, string? rawSalePrice)
    {
        Dictionary<string, string> attrs = new(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(availability))
        {
            attrs["availability"] = availability;
        }

        if (!string.IsNullOrWhiteSpace(brand))
        {
            attrs["brand"] = brand.Trim();
        }

        if (!string.IsNullOrWhiteSpace(condition))
        {
            attrs["condition"] = condition;
        }

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            attrs["image_url"] = imageUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(metaProductId))
        {
            attrs["meta_product_id"] = metaProductId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(metaCatalogueId))
        {
            attrs["meta_catalogue_id"] = metaCatalogueId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(rawPrice))
        {
            attrs["meta_price_raw"] = rawPrice.Trim();
        }

        if (!string.IsNullOrWhiteSpace(rawSalePrice))
        {
            attrs["meta_sale_price_raw"] = rawSalePrice.Trim();
        }

        return Json.Serialize(attrs);
    }

    private static string? NormalizeAvailability(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return AvailabilityMap.TryGetValue(raw.Trim(), out string? normalized) ? normalized : null;
    }

    private static string? NormalizeCondition(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string lower = raw.Trim().ToLowerInvariant();
        return lower is "new" or "refurbished" or "used" ? lower : null;
    }

    private static (string? Availability, string? Brand, string? Condition, string? ImageUrl, string? RawPrice, string? RawSalePrice) ExtractAttributes(string attributesJson)
    {
        try
        {
            System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(attributesJson);
            string? availability = doc.RootElement.TryGetProperty("availability", out System.Text.Json.JsonElement av) ? av.GetString() : null;
            string? brand = doc.RootElement.TryGetProperty("brand", out System.Text.Json.JsonElement b) ? b.GetString() : null;
            string? condition = doc.RootElement.TryGetProperty("condition", out System.Text.Json.JsonElement cond) ? cond.GetString() : null;
            string? imageUrl = doc.RootElement.TryGetProperty("image_url", out System.Text.Json.JsonElement img) ? img.GetString() : null;
            string? rawPrice = doc.RootElement.TryGetProperty("meta_price_raw", out System.Text.Json.JsonElement rp) ? rp.GetString() : null;
            string? rawSalePrice = doc.RootElement.TryGetProperty("meta_sale_price_raw", out System.Text.Json.JsonElement rsp) ? rsp.GetString() : null;
            return (availability, brand, condition, imageUrl, rawPrice, rawSalePrice);
        }
        catch
        {
            return (null, null, null, null, null, null);
        }
    }

    private static object BuildEventPayload(Product p, string? reason = null) => new
    {
        product_id = p.Id,
        catalog_id = p.CatalogId,
        sku = p.Sku,
        status = EnumNames.ToSnake(p.Status),
        version = p.Version,
        type_version = p.TypeVersion,
        product_type_id = p.ProductTypeId,
        reason,
    };
}
