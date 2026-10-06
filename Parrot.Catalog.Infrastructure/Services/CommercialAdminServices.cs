using Microsoft.EntityFrameworkCore;
using Parrot.Catalog.Application.Audit;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Events;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>Resolves "product or variant" targets and keeps the parent product's document fresh.</summary>
public abstract class CommercialServiceBase : AdminServiceBase
{
    protected CommercialServiceBase(CatalogDbContext db, ITenantContext tenant, TimeProvider clock, CatalogMetrics metrics, DerivedDataRebuilder rebuilder)
        : base(db, tenant, clock, metrics)
    {
        Rebuilder = rebuilder;
    }

    protected DerivedDataRebuilder Rebuilder { get; }

    protected async Task<(Guid CatalogId, Guid ProductId)> ResolveTargetAsync(Guid? productId, Guid? variantId, CancellationToken ct)
    {
        if ((productId is null) == (variantId is null))
        {
            throw new ValidationException("Exactly one of product_id or variant_id is required.");
        }

        if (productId is { } pid)
        {
            Product product = await Db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid && p.DeletedAt == null, ct)
                ?? throw new NotFoundException("Product", pid);
            return (product.CatalogId, product.Id);
        }

        ProductVariant variant = await Db.ProductVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Id == variantId && v.DeletedAt == null, ct)
            ?? throw new NotFoundException("Product variant", variantId!.Value);
        return (variant.CatalogId, variant.ProductId);
    }

    protected async Task<Guid> ProductOfAsync(Guid? productId, Guid? variantId, CancellationToken ct)
        => (await ResolveTargetAsync(productId, variantId, ct)).ProductId;
}

public sealed class OfferAdminService : CommercialServiceBase, IOfferAdminService
{
    public OfferAdminService(CatalogDbContext db, ITenantContext tenant, TimeProvider clock, CatalogMetrics metrics, DerivedDataRebuilder rebuilder)
        : base(db, tenant, clock, metrics, rebuilder)
    {
    }

    public Task<OfferAdminResponse> CreateAsync(CreateOfferRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        (Guid catalogId, Guid productId) = await ResolveTargetAsync(request.ProductId, request.VariantId, c);
        Offer offer = new Offer
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            CatalogId = catalogId,
            ProductId = request.ProductId,
            VariantId = request.VariantId,
            BillingPeriod = request.BillingPeriod,
            Status = request.Status,
            SortOrder = request.SortOrder,
        };
        Apply(offer, request.Name, request.Description, request.ValidFrom, request.ValidUntil, request.Eligibility);
        Stamp(offer, created: true);
        Db.Offers.Add(offer);
        await SaveAsync(c);
        await Rebuilder.RerenderIfPublishedAsync(productId, c);

        OfferAdminResponse response = Map.Offer(offer, []);
        Audit(AuditActions.Create, "offer", offer.Id, null, response);
        Enqueue(AggregateTypes.Offer, offer.Id, CatalogEventTypes.OfferChanged, new { offer_id = offer.Id, product_id = productId, status = EnumNames.ToSnake(offer.Status) });
        return response;
    }, ct);

    public Task<OfferAdminResponse> UpdateAsync(Guid id, UpdateOfferRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        Offer offer = await LoadOfferAsync(id, c);
        List<Price> prices = await PricesAsync(id, c);
        OfferAdminResponse before = Map.Offer(offer, prices);
        offer.BillingPeriod = request.BillingPeriod;
        offer.Status = request.Status;
        offer.SortOrder = request.SortOrder;
        Apply(offer, request.Name, request.Description, request.ValidFrom, request.ValidUntil, request.Eligibility);
        Stamp(offer, created: false);
        await SaveAsync(c);
        Guid productId = await ProductOfAsync(offer.ProductId, offer.VariantId, c);
        await Rebuilder.RerenderIfPublishedAsync(productId, c);

        OfferAdminResponse response = Map.Offer(offer, prices);
        Audit(AuditActions.Update, "offer", offer.Id, before, response);
        Enqueue(AggregateTypes.Offer, offer.Id, CatalogEventTypes.OfferChanged, new { offer_id = offer.Id, product_id = productId, status = EnumNames.ToSnake(offer.Status) });
        return response;
    }, ct);

    public async Task<OfferAdminResponse> GetAsync(Guid id, CancellationToken ct)
    {
        Offer offer = await LoadOfferAsync(id, ct);
        return Map.Offer(offer, await PricesAsync(id, ct));
    }

    public async Task<IReadOnlyList<OfferAdminResponse>> ListForProductAsync(Guid productId, CancellationToken ct)
    {
        List<Guid> variantIds = await Db.ProductVariants.AsNoTracking().Where(v => v.ProductId == productId && v.DeletedAt == null).Select(v => v.Id).ToListAsync(ct);
        List<Offer> offers = await Db.Offers.AsNoTracking()
            .Where(o => o.DeletedAt == null && (o.ProductId == productId || (o.VariantId != null && variantIds.Contains(o.VariantId.Value))))
            .OrderBy(o => o.SortOrder).ThenBy(o => o.Name)
            .ToListAsync(ct);
        List<Guid> offerIds = offers.Select(o => o.Id).ToList();
        List<Price> prices = await Db.Prices.AsNoTracking().Where(p => p.DeletedAt == null && offerIds.Contains(p.OfferId)).ToListAsync(ct);
        return offers.Select(o => Map.Offer(o, prices.Where(p => p.OfferId == o.Id))).ToArray();
    }

    public Task DeleteAsync(Guid id, CancellationToken ct) => WriteAsync(async c =>
    {
        Offer offer = await LoadOfferAsync(id, c);
        offer.DeletedAt = Now;
        Stamp(offer, created: false);
        await SaveAsync(c);
        Guid productId = await ProductOfAsync(offer.ProductId, offer.VariantId, c);
        await Rebuilder.RerenderIfPublishedAsync(productId, c);
        Audit(AuditActions.Delete, "offer", offer.Id, Map.Offer(offer, []), null);
        Enqueue(AggregateTypes.Offer, offer.Id, CatalogEventTypes.OfferChanged, new { offer_id = offer.Id, product_id = productId, status = "deleted" });
    }, ct);

    public Task<PriceResponse> AddPriceAsync(Guid offerId, CreatePriceRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        Offer offer = await LoadOfferAsync(offerId, c);
        Price price = new Price { Id = Guid.NewGuid(), TenantId = TenantId, CatalogId = offer.CatalogId, OfferId = offer.Id };
        Apply(price, request.Amount, request.Currency, request.PriceType, request.Status, request.ValidFrom, request.ValidUntil);
        Stamp(price, created: true);
        Db.Prices.Add(price);
        await SaveAsync(c);
        Guid productId = await ProductOfAsync(offer.ProductId, offer.VariantId, c);
        await Rebuilder.RerenderIfPublishedAsync(productId, c);

        PriceResponse response = Map.Price(price);
        Audit(AuditActions.Create, "price", price.Id, null, response);
        Enqueue(AggregateTypes.Price, price.Id, CatalogEventTypes.PriceChanged, new { price_id = price.Id, offer_id = offer.Id, product_id = productId, currency = price.Currency });
        return response;
    }, ct);

    public Task<PriceResponse> UpdatePriceAsync(Guid offerId, Guid priceId, UpdatePriceRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        Offer offer = await LoadOfferAsync(offerId, c);
        Price price = await Db.Prices.FirstOrDefaultAsync(p => p.Id == priceId && p.OfferId == offerId && p.DeletedAt == null, c)
            ?? throw new NotFoundException("Price", priceId);
        PriceResponse before = Map.Price(price);
        Apply(price, request.Amount, request.Currency, request.PriceType, request.Status, request.ValidFrom, request.ValidUntil);
        Stamp(price, created: false);
        await SaveAsync(c);
        Guid productId = await ProductOfAsync(offer.ProductId, offer.VariantId, c);
        await Rebuilder.RerenderIfPublishedAsync(productId, c);

        PriceResponse response = Map.Price(price);
        Audit(AuditActions.Update, "price", price.Id, before, response);
        Enqueue(AggregateTypes.Price, price.Id, CatalogEventTypes.PriceChanged, new { price_id = price.Id, offer_id = offer.Id, product_id = productId, currency = price.Currency });
        return response;
    }, ct);

    public Task DeletePriceAsync(Guid offerId, Guid priceId, CancellationToken ct) => WriteAsync(async c =>
    {
        Offer offer = await LoadOfferAsync(offerId, c);
        Price price = await Db.Prices.FirstOrDefaultAsync(p => p.Id == priceId && p.OfferId == offerId && p.DeletedAt == null, c)
            ?? throw new NotFoundException("Price", priceId);
        price.DeletedAt = Now;
        price.Status = PriceStatus.Inactive;
        Stamp(price, created: false);
        await SaveAsync(c);
        Guid productId = await ProductOfAsync(offer.ProductId, offer.VariantId, c);
        await Rebuilder.RerenderIfPublishedAsync(productId, c);
        Audit(AuditActions.Delete, "price", price.Id, Map.Price(price), null);
        Enqueue(AggregateTypes.Price, price.Id, CatalogEventTypes.PriceChanged, new { price_id = price.Id, offer_id = offer.Id, product_id = productId, currency = price.Currency });
    }, ct);

    private static void Apply(Offer offer, string name, string? description, DateTime validFrom, DateTime? validUntil, System.Text.Json.JsonElement? eligibility)
    {
        offer.Name = Guards.RequireText(name, "name", Limits.MaxNameLength);
        offer.Description = Guards.OptionalText(description, "description", 4000);
        offer.ValidFrom = Guards.Utc(validFrom);
        offer.ValidUntil = Guards.Utc(validUntil);
        Guards.RequireDateOrder(offer.ValidFrom, offer.ValidUntil, "valid_from", "valid_until");
        offer.Eligibility = Json.ObjectText(eligibility, "eligibility", 16_384);
    }

    private static void Apply(Price price, decimal amount, string currency, PriceType type, PriceStatus status, DateTime validFrom, DateTime? validUntil)
    {
        if (amount < 0 || amount > Limits.MaxPriceAmount)
        {
            throw new ValidationException($"amount must be between 0 and {Limits.MaxPriceAmount}.");
        }

        if (decimal.Round(amount, 4) != amount)
        {
            throw new ValidationException("amount may have at most four decimal places.");
        }

        if (!Iso4217.IsValid(currency))
        {
            throw new ValidationException("currency must be an ISO 4217 code such as NGN or USD.");
        }

        price.Amount = amount;
        price.Currency = currency;
        price.PriceType = type;
        price.Status = status;
        price.ValidFrom = Guards.Utc(validFrom);
        price.ValidUntil = Guards.Utc(validUntil);
        Guards.RequireDateOrder(price.ValidFrom, price.ValidUntil, "valid_from", "valid_until");
    }

    private async Task<Offer> LoadOfferAsync(Guid id, CancellationToken ct)
        => await Db.Offers.FirstOrDefaultAsync(o => o.Id == id && o.DeletedAt == null, ct) ?? throw new NotFoundException("Offer", id);

    private Task<List<Price>> PricesAsync(Guid offerId, CancellationToken ct)
        => Db.Prices.AsNoTracking().Where(p => p.OfferId == offerId && p.DeletedAt == null).ToListAsync(ct);
}

public sealed class AvailabilityAdminService : CommercialServiceBase, IAvailabilityAdminService
{
    public AvailabilityAdminService(CatalogDbContext db, ITenantContext tenant, TimeProvider clock, CatalogMetrics metrics, DerivedDataRebuilder rebuilder)
        : base(db, tenant, clock, metrics, rebuilder)
    {
    }

    public Task<AvailabilityResponse> CreateAsync(CreateAvailabilityRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        (Guid catalogId, Guid productId) = await ResolveTargetAsync(request.ProductId, request.VariantId, c);
        Availability row = new Availability { Id = Guid.NewGuid(), TenantId = TenantId, CatalogId = catalogId, ProductId = request.ProductId, VariantId = request.VariantId };
        Apply(row, request.Region, request.ValidFrom, request.ValidUntil, request.StockQty, request.Capacity, request.Status);
        Stamp(row, created: true);
        Db.Availability.Add(row);
        await SaveAsync(c);
        await Rebuilder.RerenderIfPublishedAsync(productId, c);
        AvailabilityResponse response = Map.Availability(row);
        Audit(AuditActions.Create, "availability", row.Id, null, response);
        Enqueue(AggregateTypes.Availability, row.Id, CatalogEventTypes.AvailabilityChanged, new { availability_id = row.Id, product_id = productId, region = row.Region });
        return response;
    }, ct);

    public Task<AvailabilityResponse> UpdateAsync(Guid id, UpdateAvailabilityRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        Availability row = await LoadAsync(id, c);
        AvailabilityResponse before = Map.Availability(row);
        Apply(row, request.Region, request.ValidFrom, request.ValidUntil, request.StockQty, request.Capacity, request.Status);
        Stamp(row, created: false);
        await SaveAsync(c);
        Guid productId = await ProductOfAsync(row.ProductId, row.VariantId, c);
        await Rebuilder.RerenderIfPublishedAsync(productId, c);
        AvailabilityResponse response = Map.Availability(row);
        Audit(AuditActions.Update, "availability", row.Id, before, response);
        Enqueue(AggregateTypes.Availability, row.Id, CatalogEventTypes.AvailabilityChanged, new { availability_id = row.Id, product_id = productId, region = row.Region });
        return response;
    }, ct);

    public async Task<IReadOnlyList<AvailabilityResponse>> ListForProductAsync(Guid productId, CancellationToken ct)
    {
        List<Guid> variantIds = await Db.ProductVariants.AsNoTracking().Where(v => v.ProductId == productId && v.DeletedAt == null).Select(v => v.Id).ToListAsync(ct);
        List<Availability> rows = await Db.Availability.AsNoTracking()
            .Where(a => a.DeletedAt == null && (a.ProductId == productId || (a.VariantId != null && variantIds.Contains(a.VariantId.Value))))
            .OrderBy(a => a.Region)
            .ToListAsync(ct);
        return rows.Select(Map.Availability).ToArray();
    }

    public Task DeleteAsync(Guid id, CancellationToken ct) => WriteAsync(async c =>
    {
        Availability row = await LoadAsync(id, c);
        row.DeletedAt = Now;
        Stamp(row, created: false);
        await SaveAsync(c);
        Guid productId = await ProductOfAsync(row.ProductId, row.VariantId, c);
        await Rebuilder.RerenderIfPublishedAsync(productId, c);
        Audit(AuditActions.Delete, "availability", row.Id, Map.Availability(row), null);
        Enqueue(AggregateTypes.Availability, row.Id, CatalogEventTypes.AvailabilityChanged, new { availability_id = row.Id, product_id = productId, region = row.Region });
    }, ct);

    private static void Apply(Availability row, string region, DateTime validFrom, DateTime? validUntil, int? stockQty, int? capacity, AvailabilityStatus status)
    {
        row.Region = Guards.RequireMatch(region, Guards.Region(), "region", "must be * or an ISO 3166-1 alpha-2 code, optionally with a subdivision (NG, NG-LA).");
        if (stockQty is < 0 || capacity is < 0)
        {
            throw new ValidationException("stock_qty and capacity must not be negative.");
        }

        row.StockQty = stockQty;
        row.Capacity = capacity;
        row.Status = status;
        row.ValidFrom = Guards.Utc(validFrom);
        row.ValidUntil = Guards.Utc(validUntil);
        Guards.RequireDateOrder(row.ValidFrom, row.ValidUntil, "valid_from", "valid_until");
    }

    private async Task<Availability> LoadAsync(Guid id, CancellationToken ct)
        => await Db.Availability.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct) ?? throw new NotFoundException("Availability", id);
}

public sealed class RelationshipAdminService : CommercialServiceBase, IRelationshipAdminService
{
    public RelationshipAdminService(CatalogDbContext db, ITenantContext tenant, TimeProvider clock, CatalogMetrics metrics, DerivedDataRebuilder rebuilder)
        : base(db, tenant, clock, metrics, rebuilder)
    {
    }

    public Task<RelationshipResponse> CreateAsync(CreateRelationshipRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        if (request.SourceId == request.TargetId)
        {
            throw new ValidationException("source_id and target_id must differ.");
        }

        Product source = await Db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.SourceId && p.DeletedAt == null, c)
            ?? throw new NotFoundException("Product", request.SourceId);
        Product target = await Db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.TargetId && p.DeletedAt == null, c)
            ?? throw new NotFoundException("Product", request.TargetId);
        if (source.CatalogId != target.CatalogId)
        {
            throw new ValidationException("Related products must belong to the same catalog.");
        }

        ProductRelationship rel = new ProductRelationship
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            CatalogId = source.CatalogId,
            SourceId = source.Id,
            TargetId = target.Id,
            RelationshipType = request.RelationshipType,
            Metadata = Json.ObjectText(request.Metadata, "metadata", 8192),
            SortOrder = request.SortOrder,
        };
        Stamp(rel, created: true);
        Db.ProductRelationships.Add(rel);
        await SaveAsync(c);
        await Rebuilder.RerenderIfPublishedAsync(source.Id, c);
        RelationshipResponse response = Map.Relationship(rel);
        Audit(AuditActions.Create, "product_relationship", rel.Id, null, response);
        Enqueue(AggregateTypes.Product, source.Id, CatalogEventTypes.ProductUpdated, new { product_id = source.Id, relationship_id = rel.Id, target_id = target.Id, relationship_type = EnumNames.ToSnake(rel.RelationshipType) });
        return response;
    }, ct);

    public async Task<IReadOnlyList<RelationshipResponse>> ListForProductAsync(Guid productId, RelationshipType? type, CancellationToken ct)
    {
        List<ProductRelationship> rows = await Db.ProductRelationships.AsNoTracking()
            .Where(r => r.DeletedAt == null && (r.SourceId == productId || r.TargetId == productId) && (type == null || r.RelationshipType == type))
            .OrderBy(r => r.SortOrder).ThenBy(r => r.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(Map.Relationship).ToArray();
    }

    public Task DeleteAsync(Guid id, CancellationToken ct) => WriteAsync(async c =>
    {
        ProductRelationship rel = await Db.ProductRelationships.FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, c)
            ?? throw new NotFoundException("Relationship", id);
        rel.DeletedAt = Now;
        Stamp(rel, created: false);
        await SaveAsync(c);
        await Rebuilder.RerenderIfPublishedAsync(rel.SourceId, c);
        Audit(AuditActions.Delete, "product_relationship", rel.Id, Map.Relationship(rel), null);
        Enqueue(AggregateTypes.Product, rel.SourceId, CatalogEventTypes.ProductUpdated, new { product_id = rel.SourceId, relationship_id = rel.Id, deleted = true });
    }, ct);
}

public sealed class MediaAdminService : CommercialServiceBase, IMediaAdminService
{
    private static readonly System.Text.RegularExpressions.Regex MimePattern = new("^[a-z]+/[a-z0-9.+-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public MediaAdminService(CatalogDbContext db, ITenantContext tenant, TimeProvider clock, CatalogMetrics metrics, DerivedDataRebuilder rebuilder)
        : base(db, tenant, clock, metrics, rebuilder)
    {
    }

    public Task<MediaResponse> CreateAsync(CreateMediaRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        Product product = await Db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProductId && p.DeletedAt == null, c)
            ?? throw new NotFoundException("Product", request.ProductId);
        if (request.VariantId is { } vid && !await Db.ProductVariants.AnyAsync(v => v.Id == vid && v.ProductId == product.Id && v.DeletedAt == null, c))
        {
            throw new ValidationException("variant_id must be a variant of product_id.");
        }

        string key = Guards.RequireText(request.S3Key, "s3_key", Limits.MaxS3KeyLength);
        string expectedPrefix = $"tenants/{TenantId:D}/";
        if (!key.StartsWith(expectedPrefix, StringComparison.Ordinal) || key.Contains("..", StringComparison.Ordinal))
        {
            throw new ValidationException($"s3_key must start with the tenant prefix '{expectedPrefix}'.");
        }

        MediaAsset media = new MediaAsset
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            CatalogId = product.CatalogId,
            ProductId = product.Id,
            VariantId = request.VariantId,
            Kind = request.Kind,
            S3Key = key,
            MimeType = Guards.RequireMatch(request.MimeType, MimePattern, "mime_type", "must be a lowercase media type such as image/png."),
            AltText = Guards.OptionalText(request.AltText, "alt_text", Limits.MaxAltTextLength),
            Title = Guards.OptionalText(request.Title, "title", Limits.MaxNameLength),
            SizeBytes = request.SizeBytes is < 0 ? throw new ValidationException("size_bytes must not be negative.") : request.SizeBytes,
            SortOrder = request.SortOrder,
        };
        Stamp(media, created: true);
        Db.Media.Add(media);
        MediaResponse response = Map.Media(media);
        Audit(AuditActions.Create, "media", media.Id, null, response);
        Enqueue(AggregateTypes.Product, product.Id, CatalogEventTypes.ProductUpdated, new { product_id = product.Id, media_id = media.Id });
        return response;
    }, ct);

    public Task<MediaResponse> UpdateAsync(Guid id, UpdateMediaRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        MediaAsset media = await LoadAsync(id, c);
        MediaResponse before = Map.Media(media);
        media.AltText = Guards.OptionalText(request.AltText, "alt_text", Limits.MaxAltTextLength);
        media.Title = Guards.OptionalText(request.Title, "title", Limits.MaxNameLength);
        media.SortOrder = request.SortOrder;
        Stamp(media, created: false);
        MediaResponse response = Map.Media(media);
        Audit(AuditActions.Update, "media", media.Id, before, response);
        Enqueue(AggregateTypes.Product, media.ProductId, CatalogEventTypes.ProductUpdated, new { product_id = media.ProductId, media_id = media.Id });
        return response;
    }, ct);

    public async Task<IReadOnlyList<MediaResponse>> ListForProductAsync(Guid productId, CancellationToken ct)
        => (await Db.Media.AsNoTracking().Where(m => m.ProductId == productId && m.DeletedAt == null).OrderBy(m => m.SortOrder).ToListAsync(ct)).Select(Map.Media).ToArray();

    public Task DeleteAsync(Guid id, CancellationToken ct) => WriteAsync(async c =>
    {
        MediaAsset media = await LoadAsync(id, c);
        media.DeletedAt = Now;
        Stamp(media, created: false);
        Audit(AuditActions.Delete, "media", media.Id, Map.Media(media), null);
        Enqueue(AggregateTypes.Product, media.ProductId, CatalogEventTypes.ProductUpdated, new { product_id = media.ProductId, media_id = media.Id, deleted = true });
    }, ct);

    private async Task<MediaAsset> LoadAsync(Guid id, CancellationToken ct)
        => await Db.Media.FirstOrDefaultAsync(m => m.Id == id && m.DeletedAt == null, ct) ?? throw new NotFoundException("Media", id);
}
