using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Pagination;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Validation;
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
    private readonly ICatalogReadService _read;
    private readonly CatalogReadDbContext _db;
    private readonly TimeProvider _clock;
    private readonly CatalogMetrics _metrics;

    public AiToolService(ICatalogReadService read, CatalogReadDbContext db, TimeProvider clock, CatalogMetrics metrics)
    {
        _read = read;
        _db = db;
        _clock = clock;
        _metrics = metrics;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<AiToolResponse<AiSearchResult>> SearchCatalogAsync(SearchCatalogToolRequest request, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Guid? category = await ResolveCategoryAsync(request.Category, ct);
        int limit = PageSize.Clamp(request.Limit, Limits.MaxAiPageSize, Limits.MaxAiPageSize);
        SearchResponse response = await _read.SearchAsync(new SearchRequest(request.Query, category, null, null, request.Filters, null, null, limit), Limits.MaxAiPageSize, ct);
        List<AiSearchHit> hits = new List<AiSearchHit>(response.Items.Count);
        foreach (SearchHit hit in response.Items)
        {
            hits.Add(new AiSearchHit(await ProductAsync(hit.Product, ct), hit.WhyMatched.Select(w => UntrustedText.Sanitize(w, 200)).ToArray()));
        }

        _metrics.SearchCompleted(sw.Elapsed.TotalMilliseconds, "ai-tool");
        return Wrap("search_catalog", new AiSearchResult(hits, hits.Count));
    }

    public async Task<AiToolResponse<AiProduct>> GetProductAsync(GetProductToolRequest request, CancellationToken ct)
    {
        ProductDetail detail = await _read.GetProductAsync(RequireId(request.Id), ct);
        return Wrap("get_product", Product(detail.Product, detail.Variants));
    }

    public async Task<AiToolResponse<AiOffersResult>> GetProductOffersAsync(GetProductOffersToolRequest request, CancellationToken ct)
    {
        IReadOnlyList<OfferView> offers = await _read.GetOffersAsync(RequireId(request.Id), request.AsOf, ct);
        AiOffer[] result = offers.Select(o => new AiOffer(
            o.Id, Text(o.Name), o.BillingPeriod, o.VariantId, o.Unit, o.PriceOnRequest,
            o.Prices.Select(p => new AiPrice(p.Amount, p.Currency, p.PriceType)).ToArray(), o.ValidFrom, o.ValidUntil)).ToArray();
        return Wrap("get_product_offers", new AiOffersResult(request.Id, result));
    }

    public async Task<AiToolResponse<AiAvailabilityResult>> GetProductAvailabilityAsync(GetProductAvailabilityToolRequest request, CancellationToken ct)
    {
        IReadOnlyList<AvailabilityView> rows = await _read.GetAvailabilityAsync(RequireId(request.Id), request.Region, null, ct);
        return Wrap("get_product_availability", new AiAvailabilityResult(request.Id, rows.Select(a => new AiAvailability(a.VariantId, a.Region, a.Mode, a.StockQty, a.LeadTimeDays, a.Capacity)).ToArray()));
    }

    public async Task<AiToolResponse<AiCompareResult>> CompareProductsAsync(CompareProductsToolRequest request, CancellationToken ct)
    {
        CompareResponse compare = await _read.CompareAsync(new CompareRequest(request.Ids ?? []), ct);
        List<AiProduct> products = new List<AiProduct>();
        foreach (ProductSummary s in compare.Products)
        {
            products.Add(Product(s, []));
        }

        AiCompareRow[] rows = compare.Rows.Select(r => new AiCompareRow(Text(r.Label).Value, r.Unit, r.Values)).ToArray();
        return Wrap("compare_products", new AiCompareResult(products, rows));
    }

    public async Task<AiToolResponse<AiRelatedResult>> GetRelatedProductsAsync(GetRelatedProductsToolRequest request, CancellationToken ct)
    {
        IReadOnlyList<RelatedProductView> related = await _read.GetRelatedAsync(RequireId(request.Id), request.RelationshipType, ct);
        return Wrap("get_related_products", new AiRelatedResult(request.Id, related.Select(r => new AiRelated(r.RelationshipType, Product(r.Product, []))).ToArray()));
    }

    private async Task<AiProduct> ProductAsync(ProductSummary summary, CancellationToken ct)
    {
        List<Domain.Entities.ProductVariant> variants = await _db.ProductVariants.Where(v => v.ProductId == summary.Id).OrderBy(v => v.SortOrder).ToListAsync(ct);
        if (variants.Count == 0)
        {
            return Product(summary, []);
        }

        ProductDetail detail = await _read.GetProductAsync(summary.Id, ct);
        return Product(summary, detail.Variants);
    }

    private static AiProduct Product(ProductSummary s, IReadOnlyList<VariantView> variants) => new(
        s.Id,
        s.Sku,
        Text(s.Name),
        s.Description is null ? null : Text(s.Description),
        s.Type.Key,
        s.Type.Kind,
        s.Category?.Path.Select(p => UntrustedText.Sanitize(p, 128)).ToArray() ?? [],
        s.Attributes.Select(Attribute).ToArray(),
        variants.Select(v => new AiVariant(v.Id, v.Sku, Text(v.Name), v.Attributes.Select(Attribute).ToArray())).ToArray());

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
