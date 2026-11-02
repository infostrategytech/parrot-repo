using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Pagination;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>
/// The dashboard's items. Each write runs the existing admin services inside one
/// transaction, so an item is saved whole or not at all, and keeps their validation,
/// audit, derived data and events. The workspace's catalog and its two plain product
/// types are created the first time they are needed.
/// </summary>
public sealed class ItemService : AdminServiceBase, IItemService
{
    public const string CatalogKey = "main";
    public const string ProductTypeKey = "general_product";
    public const string ServiceTypeKey = "general_service";
    public const string DefaultCurrency = "NGN";

    // The region an item's own availability row covers; rows for other regions are left alone.
    private const string AllRegions = "*";
    private const string OfferName = "Standard";
    private const int MaxVariants = 50;

    private static readonly JsonElement NoAttributes = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly ICatalogAdminService _catalogs;
    private readonly IProductTypeAdminService _types;
    private readonly ICategoryAdminService _categories;
    private readonly IProductAdminService _products;
    private readonly IOfferAdminService _offers;
    private readonly IAvailabilityAdminService _availability;

    public ItemService(
        CatalogDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        CatalogMetrics metrics,
        ICatalogAdminService catalogs,
        IProductTypeAdminService types,
        ICategoryAdminService categories,
        IProductAdminService products,
        IOfferAdminService offers,
        IAvailabilityAdminService availability)
        : base(db, tenant, clock, metrics)
    {
        _catalogs = catalogs;
        _types = types;
        _categories = categories;
        _products = products;
        _offers = offers;
        _availability = availability;
    }

    public async Task<ItemListResponse> ListAsync(ItemListQuery query, CancellationToken ct)
    {
        int pageSize = PageSize.Clamp(query.PageSize, Limits.MaxPageSize, Limits.DefaultPageSize);
        int page = Math.Max(query.Page ?? 1, 1);

        IQueryable<Product> scope = Db.Products.AsNoTracking().Where(p => p.DeletedAt == null && p.Status != ProductStatus.Retired);
        if (query.CategoryId is { } categoryId)
        {
            scope = scope.Where(p => p.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            string text = Guards.RequireText(query.Query, "q", Limits.MaxQueryLength);
            string pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            scope = scope.Where(p => EF.Functions.ILike(p.Name, pattern));
        }

        Expression<Func<Product, bool>> available = IsAvailable();
        IQueryable<Product> live = scope.Where(p => p.Status == ProductStatus.Published);
        ItemCounts counts = new ItemCounts(
            await scope.CountAsync(ct),
            await live.Where(available).CountAsync(ct),
            await live.Where(Not(available)).CountAsync(ct),
            await scope.CountAsync(p => p.Status == ProductStatus.Draft || p.Status == ProductStatus.Review, ct));

        IQueryable<Product> filtered = query.Filter switch
        {
            ItemFilter.Available => live.Where(available),
            ItemFilter.Unavailable => live.Where(Not(available)),
            ItemFilter.Draft => scope.Where(p => p.Status == ProductStatus.Draft || p.Status == ProductStatus.Review),
            _ => scope,
        };
        int total = query.Filter switch
        {
            ItemFilter.Available => counts.Available,
            ItemFilter.Unavailable => counts.Unavailable,
            ItemFilter.Draft => counts.Draft,
            _ => counts.All,
        };

        List<Product> rows = await filtered
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return new ItemListResponse(await ShapeAsync(rows, ct), total, page, pageSize, counts);
    }

    public async Task<ItemResponse> GetAsync(Guid id, CancellationToken ct)
    {
        Product product = await Db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw new NotFoundException("Item", id);
        return (await ShapeAsync([product], ct))[0];
    }

    public Task<ItemResponse> CreateAsync(SaveItemRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        RequireSaveableStatus(request.Status);
        ValidatePricing(request.Pricing);
        ValidateAvailability(request.Availability);

        CatalogEntity catalog = await EnsureCatalogAsync(c);
        Guid typeId = await EnsureTypeAsync(request.Kind, c);
        string name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength);
        ProductAdminResponse product = await _products.CreateAsync(
            new CreateProductRequest(catalog.Id, typeId, request.CategoryId, NewSku("ITM"), name, NoAttributes, await FreeSlugAsync(catalog.Id, name, c), request.Description),
            c);

        await ApplyPricingAsync(product.Id, catalog, request.Pricing, c);
        await ApplyAvailabilityAsync(product.Id, request.Availability, c);
        if (request.Status == ProductStatus.Published)
        {
            await _products.PublishAsync(product.Id, c);
        }

        return await GetAsync(product.Id, c);
    }, ct);

    public Task<ItemResponse> UpdateAsync(Guid id, SaveItemRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        RequireSaveableStatus(request.Status);
        ValidatePricing(request.Pricing);
        ValidateAvailability(request.Availability);

        ProductAdminResponse product = await _products.GetAsync(id, c);
        if (KindOf(product.Type.Kind) != request.Kind)
        {
            throw new ValidationException("kind cannot change once an item exists; add a new item instead.");
        }

        if (product.Status == ProductStatus.Published && request.Status == ProductStatus.Draft)
        {
            throw new ConflictException("A live item cannot go back to being a draft.");
        }

        CatalogEntity catalog = await Db.Catalogs.AsNoTracking().FirstAsync(x => x.Id == product.CatalogId, c);
        await _products.UpdateAsync(id, new UpdateProductRequest(request.CategoryId, request.Name, product.Attributes, Description: request.Description), c);
        await ApplyPricingAsync(id, catalog, request.Pricing, c);
        await ApplyAvailabilityAsync(id, request.Availability, c);
        if (request.Status == ProductStatus.Published && product.Status != ProductStatus.Published)
        {
            await _products.PublishAsync(id, c);
        }

        return await GetAsync(id, c);
    }, ct);

    public Task<ItemResponse> SetAvailabilityAsync(Guid id, ItemAvailabilityInput request, CancellationToken ct) => WriteAsync(async c =>
    {
        ValidateAvailability(request);
        if (!await Db.Products.AnyAsync(p => p.Id == id && p.DeletedAt == null, c))
        {
            throw new NotFoundException("Item", id);
        }

        await ApplyAvailabilityAsync(id, request, c);
        return await GetAsync(id, c);
    }, ct);

    public Task DeleteAsync(Guid id, CancellationToken ct) => _products.DeleteAsync(id, ct);

    public async Task<IReadOnlyList<ItemCategory>> ListCategoriesAsync(CancellationToken ct)
    {
        CatalogEntity? catalog = await Db.Catalogs.AsNoTracking().FirstOrDefaultAsync(x => x.Key == CatalogKey && x.DeletedAt == null, ct);
        if (catalog is null)
        {
            return [];
        }

        return await Db.Categories.AsNoTracking()
            .Where(x => x.CatalogId == catalog.Id && x.DeletedAt == null)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new ItemCategory(x.Id, x.Name))
            .ToListAsync(ct);
    }

    public Task<ItemCategory> CreateCategoryAsync(CreateItemCategoryRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        string name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength);
        CatalogEntity catalog = await EnsureCatalogAsync(c);
        if (await Db.Categories.AnyAsync(x => x.CatalogId == catalog.Id && x.DeletedAt == null && x.Name.ToLower() == name.ToLower(), c))
        {
            throw new ConflictException("A category with that name already exists.");
        }

        // Keys and slugs stay unique even against deleted categories, so look for a free one.
        string baseKey = Guards.Slugify(name).Replace('-', '_');
        baseKey = char.IsAsciiLetter(baseKey[0]) ? baseKey : "c_" + baseKey;
        baseKey = baseKey[..Math.Min(baseKey.Length, 56)];
        List<string> taken = await Db.Categories.AsNoTracking().Where(x => x.CatalogId == catalog.Id).Select(x => x.Key).ToListAsync(c);
        string key = baseKey;
        for (int n = 2; taken.Contains(key); n++)
        {
            key = $"{baseKey}_{n}";
        }

        CategoryResponse created = await _categories.CreateAsync(new CreateCategoryRequest(catalog.Id, null, key, name, key.Replace('_', '-')), c);
        return new ItemCategory(created.Id, created.Name);
    }, ct);

    // ---- writing an item's parts ----------------------------------------------

    private async Task ApplyPricingAsync(Guid productId, CatalogEntity catalog, ItemPricingInput pricing, CancellationToken ct)
    {
        string currency = pricing.Currency ?? catalog.DefaultCurrency;
        List<ProductVariant> variants = await Db.ProductVariants.AsNoTracking().Where(v => v.ProductId == productId && v.DeletedAt == null).ToListAsync(ct);
        List<Guid> variantIds = variants.Select(v => v.Id).ToList();
        List<Offer> offers = await Db.Offers.AsNoTracking()
            .Where(o => o.DeletedAt == null && (o.ProductId == productId || (o.VariantId != null && variantIds.Contains(o.VariantId.Value))))
            .ToListAsync(ct);
        List<Offer> productOffers = offers.Where(o => o.ProductId == productId).OrderBy(o => o.SortOrder).ThenBy(o => o.CreatedAt).ToList();

        if (pricing.Mode == PricingMode.Variants)
        {
            foreach (Offer offer in productOffers)
            {
                await _offers.DeleteAsync(offer.Id, ct);
            }

            HashSet<Guid> kept = pricing.Variants!.Where(v => v.Id is not null).Select(v => v.Id!.Value).ToHashSet();
            if (kept.Except(variantIds).FirstOrDefault() is { } unknown && unknown != Guid.Empty)
            {
                throw new ValidationException($"Variant {unknown} is not part of this item.");
            }

            foreach (ProductVariant gone in variants.Where(v => !kept.Contains(v.Id)))
            {
                await DeleteVariantAsync(productId, gone, offers, ct);
            }

            for (int i = 0; i < pricing.Variants!.Count; i++)
            {
                ItemVariantInput input = pricing.Variants[i];
                Guid variantId;
                if (input.Id is { } existingId)
                {
                    ProductVariant existing = variants.First(v => v.Id == existingId);
                    await _products.UpdateVariantAsync(productId, existingId, new UpdateVariantRequest(input.Name, Json.Element(existing.Attributes), VariantStatus.Active, i), ct);
                    variantId = existingId;
                }
                else
                {
                    variantId = (await _products.CreateVariantAsync(productId, new CreateVariantRequest(NewSku("VAR"), input.Name, NoAttributes, VariantStatus.Active, i), ct)).Id;
                }

                Offer? variantOffer = offers.FirstOrDefault(o => o.VariantId == variantId);
                Guid offerId = await UpsertOfferAsync(variantOffer, null, variantId, pricing.Per, priceOnRequest: false, ct);
                await SetPriceAsync(offerId, input.Amount, currency, ct);
            }

            return;
        }

        foreach (ProductVariant variant in variants)
        {
            await DeleteVariantAsync(productId, variant, offers, ct);
        }

        foreach (Offer extra in productOffers.Skip(1))
        {
            await _offers.DeleteAsync(extra.Id, ct);
        }

        Offer? current = productOffers.FirstOrDefault();
        if (pricing.Mode == PricingMode.OnRequest)
        {
            if (current is not null)
            {
                foreach (Price price in await Db.Prices.AsNoTracking().Where(p => p.OfferId == current.Id && p.DeletedAt == null).ToListAsync(ct))
                {
                    await _offers.DeletePriceAsync(current.Id, price.Id, ct);
                }
            }

            await UpsertOfferAsync(current, productId, null, null, priceOnRequest: true, ct);
            return;
        }

        Guid singleOffer = await UpsertOfferAsync(current, productId, null, pricing.Per, priceOnRequest: false, ct);
        await SetPriceAsync(singleOffer, pricing.Amount!.Value, currency, ct);
    }

    private async Task DeleteVariantAsync(Guid productId, ProductVariant variant, List<Offer> offers, CancellationToken ct)
    {
        foreach (Offer offer in offers.Where(o => o.VariantId == variant.Id))
        {
            await _offers.DeleteAsync(offer.Id, ct);
        }

        await _products.DeleteVariantAsync(productId, variant.Id, ct);
    }

    private async Task<Guid> UpsertOfferAsync(Offer? offer, Guid? productId, Guid? variantId, string? unit, bool priceOnRequest, CancellationToken ct)
    {
        if (offer is null)
        {
            return (await _offers.CreateAsync(
                new CreateOfferRequest(productId, variantId, OfferName, BillingPeriod.OneTime, Now, Status: OfferStatus.Active, Unit: unit, PriceOnRequest: priceOnRequest),
                ct)).Id;
        }

        await _offers.UpdateAsync(
            offer.Id,
            new UpdateOfferRequest(offer.Name, offer.BillingPeriod, offer.ValidFrom, offer.ValidUntil, offer.Description, OfferStatus.Active, Json.Element(offer.Eligibility), offer.SortOrder, unit, priceOnRequest),
            ct);
        return offer.Id;
    }

    /// <summary>Keeps one current list price in the currency. Prices in other currencies and other price types are left alone.</summary>
    private async Task SetPriceAsync(Guid offerId, decimal amount, string currency, CancellationToken ct)
    {
        Price? current = await Db.Prices.AsNoTracking()
            .Where(p => p.OfferId == offerId && p.DeletedAt == null && p.Status == PriceStatus.Active && p.Currency == currency && p.PriceType == PriceType.List)
            .OrderByDescending(p => p.ValidFrom)
            .FirstOrDefaultAsync(ct);
        if (current is null)
        {
            await _offers.AddPriceAsync(offerId, new CreatePriceRequest(amount, currency, PriceType.List, Now), ct);
        }
        else if (current.Amount != amount)
        {
            await _offers.UpdatePriceAsync(offerId, current.Id, new UpdatePriceRequest(amount, currency, PriceType.List, current.ValidFrom, current.ValidUntil, PriceStatus.Active), ct);
        }
    }

    private async Task ApplyAvailabilityAsync(Guid productId, ItemAvailabilityInput input, CancellationToken ct)
    {
        Availability? row = await Db.Availability.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ProductId == productId && a.DeletedAt == null && a.Region == AllRegions, ct);
        AvailabilityStatus status = input.Available ? AvailabilityStatus.Active : AvailabilityStatus.Inactive;
        if (row is null)
        {
            await _availability.CreateAsync(
                new CreateAvailabilityRequest(productId, null, AllRegions, Now, StockQty: input.Quantity, Status: status, Mode: input.Mode, LeadTimeDays: input.LeadTimeDays),
                ct);
            return;
        }

        await _availability.UpdateAsync(
            row.Id,
            new UpdateAvailabilityRequest(AllRegions, row.ValidFrom, row.ValidUntil, input.Quantity, row.Capacity, status, input.Mode, input.LeadTimeDays),
            ct);
    }

    // ---- defaults ------------------------------------------------------------

    private async Task<CatalogEntity> EnsureCatalogAsync(CancellationToken ct)
    {
        CatalogEntity? catalog = await Db.Catalogs.AsNoTracking().FirstOrDefaultAsync(x => x.Key == CatalogKey && x.DeletedAt == null, ct);
        if (catalog is not null)
        {
            return catalog;
        }

        CatalogResponse created = await _catalogs.CreateAsync(new CreateCatalogRequest(CatalogKey, "Products and services", DefaultCurrency), ct);
        return await Db.Catalogs.AsNoTracking().FirstAsync(x => x.Id == created.Id, ct);
    }

    private async Task<Guid> EnsureTypeAsync(ItemKind kind, CancellationToken ct)
    {
        string key = kind == ItemKind.Service ? ServiceTypeKey : ProductTypeKey;
        ProductType? type = await Db.ProductTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Key == key, ct);
        if (type is not null)
        {
            return type.Id;
        }

        CreateProductTypeRequest request = kind == ItemKind.Service
            ? new CreateProductTypeRequest(key, "Service", ProductTypeKind.Service)
            : new CreateProductTypeRequest(key, "Product", ProductTypeKind.Good);
        return (await _types.CreateAsync(request, ct)).Id;
    }

    private async Task<string> FreeSlugAsync(Guid catalogId, string name, CancellationToken ct)
    {
        string baseSlug = Guards.Slugify(name);
        baseSlug = baseSlug[..Math.Min(baseSlug.Length, Limits.MaxSlugLength - 8)].TrimEnd('-');
        List<string> taken = await Db.Products.AsNoTracking()
            .Where(p => p.CatalogId == catalogId && p.DeletedAt == null && p.Slug.StartsWith(baseSlug))
            .Select(p => p.Slug)
            .ToListAsync(ct);
        string slug = baseSlug;
        for (int n = 2; taken.Contains(slug); n++)
        {
            slug = $"{baseSlug}-{n}";
        }

        return slug;
    }

    private static string NewSku(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 11)].ToUpperInvariant();

    // ---- validation ----------------------------------------------------------

    private void RequireSaveableStatus(ProductStatus status)
    {
        if (status is not (ProductStatus.Draft or ProductStatus.Published))
        {
            throw new ValidationException("status must be draft or published.");
        }

        if (status == ProductStatus.Published && !CatalogScopes.Grants(Tenant.Scopes, CatalogScopes.Publish))
        {
            throw new ForbiddenException("Making an item live needs permission to publish; save it as a draft instead.");
        }
    }

    private static void ValidatePricing(ItemPricingInput? pricing)
    {
        if (pricing is null)
        {
            throw new ValidationException("pricing is required.");
        }

        int variantCount = pricing.Variants?.Count ?? 0;
        switch (pricing.Mode)
        {
            case PricingMode.Single:
                if (pricing.Amount is null)
                {
                    throw new ValidationException("pricing.amount is required for a single price.");
                }

                if (variantCount > 0)
                {
                    throw new ValidationException("pricing.variants is only for mode variants.");
                }

                break;
            case PricingMode.Variants:
                if (variantCount == 0)
                {
                    throw new ValidationException("pricing.variants needs at least one variant.");
                }

                if (variantCount > MaxVariants)
                {
                    throw new ValidationException($"An item can have at most {MaxVariants} variants.");
                }

                if (pricing.Amount is not null)
                {
                    throw new ValidationException("pricing.amount is not used with variants; give each variant its amount.");
                }

                if (pricing.Variants!.Where(v => v.Id is not null).GroupBy(v => v.Id).Any(g => g.Count() > 1))
                {
                    throw new ValidationException("Each variant id may appear once.");
                }

                foreach (ItemVariantInput variant in pricing.Variants!)
                {
                    Guards.RequireText(variant.Name, "pricing.variants.name", Limits.MaxNameLength);
                }

                break;
            case PricingMode.OnRequest:
                if (pricing.Amount is not null || variantCount > 0 || pricing.Per is not null)
                {
                    throw new ValidationException("A price on request has no amount, per or variants.");
                }

                break;
        }
    }

    private static void ValidateAvailability(ItemAvailabilityInput? availability)
    {
        if (availability is null)
        {
            throw new ValidationException("availability is required.");
        }

        if (availability.Quantity is not null && availability.Mode != AvailabilityMode.InStock)
        {
            throw new ValidationException("availability.quantity is only for mode in_stock.");
        }

        if (availability.Quantity is < 0)
        {
            throw new ValidationException("availability.quantity must not be negative.");
        }
    }

    // ---- reading -------------------------------------------------------------

    /// <summary>Live and on offer: no row of its own says it is off or sold out. Items with no availability recorded count as available.</summary>
    private Expression<Func<Product, bool>> IsAvailable()
        => p => !Db.Availability.Any(a => a.ProductId == p.Id && a.DeletedAt == null
            && (a.Status == AvailabilityStatus.Inactive || (a.Mode == AvailabilityMode.InStock && a.StockQty == 0)));

    private static Expression<Func<Product, bool>> Not(Expression<Func<Product, bool>> predicate)
        => Expression.Lambda<Func<Product, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    private static ItemKind KindOf(ProductTypeKind kind) => kind == ProductTypeKind.Service ? ItemKind.Service : ItemKind.Product;

    private async Task<IReadOnlyList<ItemResponse>> ShapeAsync(List<Product> products, CancellationToken ct)
    {
        if (products.Count == 0)
        {
            return [];
        }

        List<Guid> ids = products.Select(p => p.Id).ToList();
        List<Guid> typeIds = products.Select(p => p.ProductTypeId).Distinct().ToList();
        List<Guid> catalogIds = products.Select(p => p.CatalogId).Distinct().ToList();
        List<Guid> categoryIds = products.Where(p => p.CategoryId != null).Select(p => p.CategoryId!.Value).Distinct().ToList();

        Dictionary<Guid, ProductTypeKind> kinds = await Db.ProductTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Kind, ct);
        Dictionary<Guid, string> currencies = await Db.Catalogs.AsNoTracking().Where(x => catalogIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DefaultCurrency, ct);
        Dictionary<Guid, string> categories = await Db.Categories.AsNoTracking().Where(x => categoryIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        List<ProductVariant> variants = await Db.ProductVariants.AsNoTracking().Where(v => ids.Contains(v.ProductId) && v.DeletedAt == null).ToListAsync(ct);
        List<Guid> variantIds = variants.Select(v => v.Id).ToList();
        List<Offer> offers = await Db.Offers.AsNoTracking()
            .Where(o => o.DeletedAt == null && ((o.ProductId != null && ids.Contains(o.ProductId.Value)) || (o.VariantId != null && variantIds.Contains(o.VariantId.Value))))
            .ToListAsync(ct);
        List<Guid> offerIds = offers.Select(o => o.Id).ToList();
        List<Price> prices = await Db.Prices.AsNoTracking()
            .Where(p => offerIds.Contains(p.OfferId) && p.DeletedAt == null && p.Status == PriceStatus.Active && p.PriceType == PriceType.List)
            .ToListAsync(ct);
        List<Availability> availability = await Db.Availability.AsNoTracking()
            .Where(a => a.ProductId != null && ids.Contains(a.ProductId.Value) && a.DeletedAt == null)
            .ToListAsync(ct);

        return products.Select(p =>
        {
            string currency = currencies.GetValueOrDefault(p.CatalogId, DefaultCurrency);
            decimal? AmountOf(Offer? offer)
            {
                if (offer is null)
                {
                    return null;
                }

                List<Price> own = prices.Where(x => x.OfferId == offer.Id).OrderByDescending(x => x.ValidFrom).ToList();
                return (own.FirstOrDefault(x => x.Currency == currency) ?? own.FirstOrDefault())?.Amount;
            }

            List<ProductVariant> ownVariants = variants.Where(v => v.ProductId == p.Id).OrderBy(v => v.SortOrder).ThenBy(v => v.Sku, StringComparer.Ordinal).ToList();
            Offer? productOffer = offers.Where(o => o.ProductId == p.Id).OrderBy(o => o.SortOrder).ThenBy(o => o.CreatedAt).FirstOrDefault();

            ItemPricing pricing;
            if (ownVariants.Count > 0)
            {
                List<ItemVariant> shaped = ownVariants.Select(v => new ItemVariant(v.Id, v.Name, AmountOf(offers.FirstOrDefault(o => o.VariantId == v.Id)))).ToList();
                List<decimal> amounts = shaped.Where(v => v.Amount is not null).Select(v => v.Amount!.Value).ToList();
                string? per = offers.FirstOrDefault(o => o.VariantId != null && ownVariants.Any(v => v.Id == o.VariantId))?.Unit;
                pricing = new ItemPricing(PricingMode.Variants, currency, null, per, amounts.Count > 0 ? amounts.Min() : null, amounts.Count > 0 ? amounts.Max() : null, shaped);
            }
            else if (productOffer is { PriceOnRequest: true })
            {
                pricing = new ItemPricing(PricingMode.OnRequest, currency, null, null, null, null, []);
            }
            else if (AmountOf(productOffer) is { } amount)
            {
                pricing = new ItemPricing(PricingMode.Single, currency, amount, productOffer!.Unit, amount, amount, []);
            }
            else
            {
                pricing = new ItemPricing(null, currency, null, null, null, null, []);
            }

            Availability? row = availability.Where(a => a.ProductId == p.Id).OrderBy(a => a.Region == AllRegions ? 0 : 1).ThenBy(a => a.Region, StringComparer.Ordinal).FirstOrDefault();
            ItemAvailability? shapedAvailability = row is null
                ? null
                : new ItemAvailability(row.Mode, row.Status == AvailabilityStatus.Active, row.StockQty, row.LeadTimeDays);
            ItemCategory? category = p.CategoryId is { } cid && categories.TryGetValue(cid, out string? categoryName) ? new ItemCategory(cid, categoryName) : null;

            return new ItemResponse(p.Id, p.Name, KindOf(kinds[p.ProductTypeId]), category, p.Description, p.Status, pricing, shapedAvailability, p.CreatedAt, p.UpdatedAt);
        }).ToList();
    }
}
