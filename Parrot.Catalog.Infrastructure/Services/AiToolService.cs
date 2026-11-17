using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Pagination;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>
/// The LLM-facing surface. It is a projection of the read service: tenant from the
/// token (the read connection already carries it), internal fields dropped, free text
/// sanitised and wrapped as untrusted, limits tightened, why_matched preserved.
/// </summary>
public sealed class AiToolService : IAiToolService
{
    /// <summary>Photos per product in an answer: as many as an agent reply can carry.</summary>
    public const int MaxPhotosPerProduct = 3;

    private readonly ICatalogReadService _read;
    private readonly CatalogReadDbContext _db;
    private readonly TimeProvider _clock;
    private readonly CatalogMetrics _metrics;
    private readonly IItemPhotoStorage _photos;

    public AiToolService(ICatalogReadService read, CatalogReadDbContext db, TimeProvider clock, CatalogMetrics metrics, IItemPhotoStorage photos)
    {
        _read = read;
        _db = db;
        _clock = clock;
        _metrics = metrics;
        _photos = photos;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<AiToolResponse<AiSearchResult>> SearchCatalogAsync(SearchCatalogToolRequest request, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Guid? category = await ResolveCategoryAsync(request.Category, ct);
        int limit = PageSize.Clamp(request.Limit, Limits.MaxAiPageSize, Limits.MaxAiPageSize);
        SearchResponse response = await _read.SearchAsync(new SearchRequest(request.Query, category, null, null, request.Filters, null, request.Cursor, limit), Limits.MaxAiPageSize, ct);
        List<AiSearchHit> hits = new List<AiSearchHit>(response.Items.Count);
        Dictionary<Guid, AiImage[]> photos = await ImagesAsync(response.Items.Select(h => h.Product.Id), ct);
        foreach (SearchHit hit in response.Items)
        {
            hits.Add(new AiSearchHit(await ProductAsync(hit.Product, photos, ct), hit.WhyMatched.Select(w => UntrustedText.Sanitize(w, 200)).ToArray()));
        }

        _metrics.SearchCompleted(sw.Elapsed.TotalMilliseconds, "ai-tool");
        return Wrap("search_catalog", new AiSearchResult(hits, hits.Count, response.NextCursor));
    }

    public async Task<AiToolResponse<AiProduct>> GetProductAsync(GetProductToolRequest request, CancellationToken ct)
    {
        ProductDetail detail = await _read.GetProductAsync(RequireId(request.Id), ct);
        Dictionary<Guid, AiImage[]> photos = await ImagesAsync([detail.Product.Id], ct);
        return Wrap("get_product", Product(detail.Product, detail.Variants, photos));
    }

    public async Task<AiToolResponse<AiOffersResult>> GetProductOffersAsync(GetProductOffersToolRequest request, CancellationToken ct)
    {
        IReadOnlyList<OfferView> offers = await _read.GetOffersAsync(RequireId(request.Id), request.AsOf, ct);
        AiOffer[] result = offers.Select(o => new AiOffer(
            o.Id, Text(o.Name), o.BillingPeriod, o.VariantId, o.Unit, o.PriceOnRequest,
            o.Prices.Select(p => new AiPrice(p.Amount, p.Currency, p.PriceType)).ToArray(), o.ValidFrom, o.ValidUntil)).ToArray();
        return Wrap("get_product_offers", new AiOffersResult(request.Id, result));
    }

    /// <summary>
    /// Price and availability for several products in a few queries, judged the way the
    /// dashboard judges them: an item is available unless a row says it is off, or it is
    /// counted stock with none left; an option likewise, and only while its item is.
    /// Hidden items are not on sale, so they are reported with the unknown ones.
    /// </summary>
    public async Task<AiToolResponse<AiPricesResult>> GetPricesAsync(GetPricesToolRequest request, CancellationToken ct)
    {
        List<Guid> ids = (request.Ids ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new ValidationException("ids needs at least one product id.");
        }

        if (ids.Count > Limits.MaxPriceItems)
        {
            throw new ValidationException($"ids may hold at most {Limits.MaxPriceItems} products.");
        }

        if (request.MinPrice is < 0 || request.MaxPrice is < 0)
        {
            throw new ValidationException("min_price and max_price can't be negative.");
        }

        if (request.MinPrice > request.MaxPrice)
        {
            throw new ValidationException("min_price can't be more than max_price.");
        }

        bool ranged = request.MinPrice is not null || request.MaxPrice is not null;

        DateTime now = Now;
        List<Domain.Entities.Product> products = await _db.Products.AsNoTracking().Where(p => ids.Contains(p.Id) && !p.Hidden).ToListAsync(ct);
        List<Guid> found = products.Select(p => p.Id).ToList();
        List<Guid> catalogIds = products.Select(p => p.CatalogId).Distinct().ToList();
        Dictionary<Guid, string> currencies = await _db.Catalogs.AsNoTracking().Where(c => catalogIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.DefaultCurrency, ct);
        List<Domain.Entities.ProductVariant> variants = await _db.ProductVariants.AsNoTracking()
            .Where(v => found.Contains(v.ProductId)).OrderBy(v => v.SortOrder).ThenBy(v => v.Sku).ToListAsync(ct);
        List<Guid> variantIds = variants.Select(v => v.Id).ToList();
        List<Domain.Entities.Offer> offers = await _db.Offers.AsNoTracking()
            .Where(o => o.Status == OfferStatus.Active && o.ValidFrom <= now && (o.ValidUntil == null || o.ValidUntil > now))
            .Where(o => (o.ProductId != null && found.Contains(o.ProductId.Value)) || (o.VariantId != null && variantIds.Contains(o.VariantId.Value)))
            .OrderBy(o => o.SortOrder).ToListAsync(ct);
        List<Guid> offerIds = offers.Select(o => o.Id).ToList();
        List<Domain.Entities.Price> prices = await _db.Prices.AsNoTracking()
            .Where(p => offerIds.Contains(p.OfferId) && p.Status == PriceStatus.Active && p.PriceType != PriceType.Contract
                && p.ValidFrom <= now && (p.ValidUntil == null || p.ValidUntil > now))
            .ToListAsync(ct);
        List<Domain.Entities.Availability> rows = await _db.Availability.AsNoTracking()
            .Where(a => a.ValidFrom <= now && (a.ValidUntil == null || a.ValidUntil > now))
            .Where(a => (a.ProductId != null && found.Contains(a.ProductId.Value)) || (a.VariantId != null && variantIds.Contains(a.VariantId.Value)))
            .ToListAsync(ct);
        await TakeOffHeldAsync(rows, found, now, ct);
        Dictionary<Guid, AiImage[]> photos = await ImagesAsync(found, ct);

        List<AiItemPrice> items = new List<AiItemPrice>(products.Count);
        foreach (Domain.Entities.Product product in products)
        {
            string currency = currencies.GetValueOrDefault(product.CatalogId) ?? "NGN";
            List<Domain.Entities.Availability> own = rows.Where(a => a.ProductId == product.Id && a.VariantId == null).ToList();
            bool productAvailable = IsAvailable(own);
            Domain.Entities.Availability? stock = own.Where(a => a.Status == AvailabilityStatus.Active).OrderBy(a => a.Region == "*" ? 0 : 1).FirstOrDefault();
            Domain.Entities.Offer? productOffer = offers.FirstOrDefault(o => o.ProductId == product.Id && o.VariantId == null);

            List<AiVariantPrice> options = new List<AiVariantPrice>();
            foreach (Domain.Entities.ProductVariant variant in variants.Where(v => v.ProductId == product.Id))
            {
                List<Domain.Entities.Availability> variantRows = rows.Where(a => a.VariantId == variant.Id).ToList();
                Domain.Entities.Offer? offer = offers.FirstOrDefault(o => o.VariantId == variant.Id) ?? productOffer;
                (decimal? price, decimal? was) = PriceOf(offer, prices, currency);
                bool optionAvailable = productAvailable && IsAvailable(variantRows);
                options.Add(new AiVariantPrice(
                    variant.Id,
                    Text(variant.Name),
                    price,
                    was,
                    optionAvailable,
                    optionAvailable ? variantRows.FirstOrDefault(a => a.Status == AvailabilityStatus.Active)?.StockQty : 0,
                    InRange(price, request.MinPrice, request.MaxPrice)));
            }

            decimal? itemPrice;
            decimal? itemWas;
            if (options.Count > 0)
            {
                AiVariantPrice? cheapest = options.Where(o => o.Available && o.Price is not null).MinBy(o => o.Price)
                    ?? options.Where(o => o.Price is not null).MinBy(o => o.Price);
                (itemPrice, itemWas) = (cheapest?.Price, cheapest?.WasPrice);
            }
            else
            {
                (itemPrice, itemWas) = PriceOf(productOffer, prices, currency);
            }

            bool available = productAvailable && (options.Count == 0 || options.Any(o => o.Available));
            bool onRequest = itemPrice is null && offers.Any(o => o.PriceOnRequest && (o.ProductId == product.Id || variants.Any(v => v.ProductId == product.Id && v.Id == o.VariantId)));

            // With options, the item is in range when an option that can be had is (any option, when none can).
            bool? inRange = InRange(itemPrice, request.MinPrice, request.MaxPrice);
            if (ranged && options.Count > 0 && options.Any(o => o.Price is not null))
            {
                IEnumerable<AiVariantPrice> candidates = options.Any(o => o.Available && o.Price is not null) ? options.Where(o => o.Available) : options;
                inRange = candidates.Any(o => o.InRange == true);
            }

            // Counted for the item, or else the total of its available options when every one of them is counted.
            int? quantityLeft = stock?.Mode == AvailabilityMode.InStock ? stock.StockQty : null;
            List<AiVariantPrice> availableOptions = options.Where(o => o.Available).ToList();
            if (quantityLeft is null && availableOptions.Count > 0 && availableOptions.All(o => o.QuantityLeft is not null))
            {
                quantityLeft = availableOptions.Sum(o => o.QuantityLeft!.Value);
            }

            items.Add(new AiItemPrice(
                product.Id,
                Text(product.Name),
                available,
                onRequest,
                itemPrice,
                itemWas,
                itemPrice is null ? null : currency,
                productOffer?.Unit,
                stock?.Mode,
                available ? quantityLeft : 0,
                stock?.LeadTimeDays,
                inRange,
                options,
                photos.GetValueOrDefault(product.Id) ?? []));
        }

        List<AiItemPrice> inside = items
            .Where(i => !ranged || i.InRange == true)
            .OrderByDescending(i => i.Available)
            .ThenBy(i => i.Price is null)
            .ThenBy(i => i.Price)
            .ToList();
        List<AiItemPrice> closest = !ranged ? [] : items
            .Where(i => i.InRange != true)
            .OrderBy(i => i.Price is null)
            .ThenByDescending(i => i.Available)
            .ThenBy(i => Distance(i.Price, request.MinPrice, request.MaxPrice))
            .ToList();
        Guid[] notOnSale = ids.Where(id => !found.Contains(id)).ToArray();
        return Wrap("get_prices", new AiPricesResult(inside, closest, notOnSale));
    }

    /// <summary>How far a price is outside the range: 0 inside it.</summary>
    private static decimal Distance(decimal? price, decimal? min, decimal? max)
        => price is not { } p ? decimal.MaxValue
            : min is { } low && p < low ? low - p
            : max is { } high && p > high ? p - high
            : 0;

    /// <summary>
    /// Takes stock held for customers who are paying off what's left, on the same row the
    /// hold was placed against (the item's or the option's counted row, everywhere first).
    /// Only holds still in time count, so one nobody paid for stops counting on its own.
    /// The rows are read-only copies; nothing is saved.
    /// </summary>
    private async Task TakeOffHeldAsync(List<Domain.Entities.Availability> rows, IReadOnlyCollection<Guid> productIds, DateTime now, CancellationToken ct)
    {
        List<Guid> ids = productIds.ToList();
        var held = await _db.StockHolds
            .Where(h => ids.Contains(h.ProductId) && h.ExpiresAt > now)
            .GroupBy(h => new { h.ProductId, h.VariantId })
            .Select(g => new { g.Key.ProductId, g.Key.VariantId, Quantity = g.Sum(h => h.Quantity) })
            .ToListAsync(ct);
        List<Domain.Entities.Availability> counted = rows
            .Where(a => a.Status == AvailabilityStatus.Active && a.Mode == AvailabilityMode.InStock && a.StockQty != null)
            .OrderBy(a => a.Region == "*" ? 0 : 1)
            .ToList();
        foreach (var hold in held)
        {
            // An option without its own count draws on the product's, as the hold did.
            Domain.Entities.Availability? row = (hold.VariantId is { } v ? counted.FirstOrDefault(a => a.VariantId == v) : null)
                ?? counted.FirstOrDefault(a => a.ProductId == hold.ProductId && a.VariantId == null);
            if (row is not null)
            {
                row.StockQty = Math.Max(0, row.StockQty!.Value - hold.Quantity);
            }
        }
    }

    /// <summary>Off when a row says so, or when it counts stock and none is left; nothing recorded means on offer.</summary>
    private static bool IsAvailable(IEnumerable<Domain.Entities.Availability> rows)
        => !rows.Any(a => a.Status == AvailabilityStatus.Inactive || (a.Mode == AvailabilityMode.InStock && a.StockQty == 0));

    /// <summary>What the offer costs now in the catalog's currency: a promotion when one applies, with the usual price as was_price.</summary>
    private static (decimal? Price, decimal? Was) PriceOf(Domain.Entities.Offer? offer, List<Domain.Entities.Price> prices, string currency)
    {
        if (offer is null || offer.PriceOnRequest)
        {
            return (null, null);
        }

        List<Domain.Entities.Price> mine = prices.Where(p => p.OfferId == offer.Id).ToList();
        List<Domain.Entities.Price> inCurrency = mine.Where(p => p.Currency == currency).ToList();
        if (inCurrency.Count == 0)
        {
            inCurrency = mine;
        }

        decimal? list = inCurrency.Where(p => p.PriceType == PriceType.List).Select(p => (decimal?)p.Amount).Min();
        decimal? promo = inCurrency.Where(p => p.PriceType == PriceType.Promo).Select(p => (decimal?)p.Amount).Min();
        return promo is not null && (list is null || promo < list) ? (promo, list) : (list ?? promo, null);
    }

    private static bool? InRange(decimal? price, decimal? min, decimal? max)
        => (min is null && max is null) || price is null ? null : (min is null || price >= min) && (max is null || price <= max);

    /// <summary>
    /// Whether a product can be had now, judged as get_prices and the dashboard judge it.
    /// Sold-out rows are kept and marked, so "sold out" never reads as "nothing recorded".
    /// </summary>
    public async Task<AiToolResponse<AiAvailabilityResult>> GetProductAvailabilityAsync(GetProductAvailabilityToolRequest request, CancellationToken ct)
    {
        Guid id = RequireId(request.Id);
        string? region = request.Region;
        if (region is not null)
        {
            Guards.RequireMatch(region, Guards.Region(), "region", "must be * or an ISO 3166-1 alpha-2 code.");
        }

        bool onSale = await _db.Products.AsNoTracking().AnyAsync(p => p.Id == id && !p.Hidden, ct);
        if (!onSale)
        {
            throw new NotFoundException("Product", id);
        }

        DateTime now = Now;
        List<Guid> variantIds = await _db.ProductVariants.AsNoTracking().Where(v => v.ProductId == id).Select(v => v.Id).ToListAsync(ct);
        List<Domain.Entities.Availability> rows = await _db.Availability.AsNoTracking()
            .Where(a => a.ValidFrom <= now && (a.ValidUntil == null || a.ValidUntil > now))
            .Where(a => a.ProductId == id || (a.VariantId != null && variantIds.Contains(a.VariantId.Value)))
            .Where(a => region == null || a.Region == region || a.Region == "*")
            .OrderBy(a => a.Region)
            .ToListAsync(ct);
        await TakeOffHeldAsync(rows, [id], now, ct);

        List<Domain.Entities.Availability> own = rows.Where(a => a.VariantId == null).ToList();
        bool productAvailable = IsAvailable(own);
        bool available = productAvailable && (variantIds.Count == 0 || variantIds.Any(v => IsAvailable(rows.Where(a => a.VariantId == v))));
        Domain.Entities.Availability? stock = own.Where(a => a.Status == AvailabilityStatus.Active).OrderBy(a => a.Region == "*" ? 1 : 0).FirstOrDefault();
        int? quantityLeft = !available ? 0 : stock?.Mode == AvailabilityMode.InStock ? stock.StockQty : null;

        AiAvailability[] result = rows.Select(a => new AiAvailability(
            a.VariantId, a.Region, a.Mode, a.StockQty, a.LeadTimeDays, a.Capacity,
            (a.VariantId == null || productAvailable) && IsAvailable([a]))).ToArray();
        return Wrap("get_product_availability", new AiAvailabilityResult(id, available, quantityLeft, result));
    }

    public async Task<AiToolResponse<AiCompareResult>> CompareProductsAsync(CompareProductsToolRequest request, CancellationToken ct)
    {
        CompareResponse compare = await _read.CompareAsync(new CompareRequest(request.Ids ?? []), ct);
        Dictionary<Guid, AiImage[]> photos = await ImagesAsync(compare.Products.Select(p => p.Id), ct);
        List<AiProduct> products = new List<AiProduct>();
        foreach (ProductSummary s in compare.Products)
        {
            products.Add(Product(s, [], photos));
        }

        AiCompareRow[] rows = compare.Rows.Select(r => new AiCompareRow(Text(r.Label).Value, r.Unit, r.Values)).ToArray();
        return Wrap("compare_products", new AiCompareResult(products, rows));
    }

    public async Task<AiToolResponse<AiRelatedResult>> GetRelatedProductsAsync(GetRelatedProductsToolRequest request, CancellationToken ct)
    {
        IReadOnlyList<RelatedProductView> related = await _read.GetRelatedAsync(RequireId(request.Id), request.RelationshipType, ct);
        Dictionary<Guid, AiImage[]> photos = await ImagesAsync(related.Select(r => r.Product.Id), ct);
        return Wrap("get_related_products", new AiRelatedResult(request.Id, related.Select(r => new AiRelated(r.RelationshipType, Product(r.Product, [], photos))).ToArray()));
    }

    private async Task<AiProduct> ProductAsync(ProductSummary summary, IReadOnlyDictionary<Guid, AiImage[]> photos, CancellationToken ct)
    {
        List<Domain.Entities.ProductVariant> variants = await _db.ProductVariants.Where(v => v.ProductId == summary.Id).OrderBy(v => v.SortOrder).ToListAsync(ct);
        if (variants.Count == 0)
        {
            return Product(summary, [], photos);
        }

        ProductDetail detail = await _read.GetProductAsync(summary.Id, ct);
        return Product(summary, detail.Variants, photos);
    }

    /// <summary>
    /// Each product's first few photos, with public links, in the order the business set, in one
    /// query. Only photos the AI reported it embedded are given: those are the ones it has learned,
    /// and each comes with the link the AI says it learned it from (ours when it named none), so the
    /// links match what it holds. A photo it rejected is never sent to a customer.
    /// The read view already leaves out removed photos and products that aren't on sale.
    /// With no photo bucket set, a photo the AI named no link for has none to give, so it is left out.
    /// </summary>
    private async Task<Dictionary<Guid, AiImage[]>> ImagesAsync(IEnumerable<Guid> productIds, CancellationToken ct)
    {
        List<Guid> ids = productIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        bool ownLinks = _photos.IsConfigured;
        var media = await _db.Media.AsNoTracking()
            .Where(m => ids.Contains(m.ProductId) && m.Kind == MediaKind.Image && m.AiStatus == MediaAiStatus.Embedded)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.ProductId, m.S3Key, m.AiUrl, m.AltText })
            .ToListAsync(ct);
        return media
            .GroupBy(m => m.ProductId)
            .ToDictionary(g => g.Key, g => g
                .Select(m => new { m.Id, Url = m.AiUrl ?? (ownLinks ? _photos.UrlFor(m.S3Key) : null), m.AltText })
                .Where(m => m.Url is not null)
                .DistinctBy(m => m.Url, StringComparer.Ordinal)
                .Take(MaxPhotosPerProduct)
                .Select(m => new AiImage(m.Id, m.Url!, string.IsNullOrWhiteSpace(m.AltText) ? null : new UntrustedTextField(UntrustedText.Sanitize(m.AltText, 300))))
                .ToArray());
    }

    private static AiProduct Product(ProductSummary s, IReadOnlyList<VariantView> variants, IReadOnlyDictionary<Guid, AiImage[]> photos) => new(
        s.Id,
        s.Sku,
        Text(s.Name),
        s.Description is null ? null : Text(s.Description),
        s.Type.Key,
        s.Type.Kind,
        s.Category?.Path.Select(p => UntrustedText.Sanitize(p, 128)).ToArray() ?? [],
        s.Attributes.Select(Attribute).ToArray(),
        variants.Select(v => new AiVariant(v.Id, v.Sku, Text(v.Name), v.Attributes.Select(Attribute).ToArray())).ToArray(),
        photos.GetValueOrDefault(s.Id) ?? []);

    private static AiAttribute Attribute(AttributeValue a)
    {
        JsonElement value = a.Value;
        if (value.ValueKind == JsonValueKind.String)
        {
            value = JsonSerializer.SerializeToElement(UntrustedText.Sanitize(value.GetString(), 1024));
        }

        return new AiAttribute(UntrustedText.Sanitize(a.Label, 128), a.Unit, value);
    }

    private static UntrustedTextField Text(string value) => new(UntrustedText.Sanitize(value));

    private AiToolResponse<T> Wrap<T>(string tool, T result) => new(tool, Now, AiToolResponse<T>.StandardNotice, result);

    private static Guid RequireId(Guid id) => id == Guid.Empty ? throw new ValidationException("id is required.") : id;

    /// <summary>Accepts a category id or a category key; a key the tenant does not have is a 400, never a guess.</summary>
    private async Task<Guid?> ResolveCategoryAsync(string? category, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        if (Guid.TryParse(category, out Guid id))
        {
            return id;
        }

        Guards.RequireMatch(category, Guards.Key(), "category", "must be a category id or key.");
        Guid? found = await _db.Categories.Where(c => c.Key == category).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        return found ?? throw new ValidationException($"category '{category}' does not exist.");
    }
}
