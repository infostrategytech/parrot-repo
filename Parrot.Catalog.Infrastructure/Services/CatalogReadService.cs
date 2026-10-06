using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Filters;
using Parrot.Catalog.Application.Pagination;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>
/// Every read goes through the reader connection and the v_* views, so only published
/// rows of the caller's tenant can ever be returned. Search compiles to parameterised
/// SQL over the derived index; keys and values are parameters, never text.
/// </summary>
public sealed class CatalogReadService : ICatalogReadService
{
    private const string IndexRelation = "catalog.v_product_attribute_index";
    private static readonly string[] Sorts = ["relevance", "name", "published_at", "created_at"];

    private readonly CatalogReadDbContext _db;
    private readonly IReadSchemaRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly CatalogMetrics _metrics;

    public CatalogReadService(CatalogReadDbContext db, IReadSchemaRegistry registry, TimeProvider clock, CatalogMetrics metrics)
    {
        _db = db;
        _registry = registry;
        _clock = clock;
        _metrics = metrics;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<PagedResponse<ProductSummary>> ListProductsAsync(ReadListQuery query, CancellationToken ct)
    {
        int limit = PageSize.Clamp(query.Limit, Limits.MaxPageSize, Limits.DefaultPageSize);
        Cursor? cursor = Cursor.Decode(query.Cursor, "published_at");
        IQueryable<Product> q = _db.Products;
        if (query.CatalogId is { } catalogId)
        {
            q = q.Where(p => p.CatalogId == catalogId);
        }

        if (query.ProductTypeId is { } typeId)
        {
            q = q.Where(p => p.ProductTypeId == typeId);
        }

        if (query.CategoryId is { } categoryId)
        {
            IQueryable<Guid> descendants = _db.CategoryClosure.Where(c => c.AncestorId == categoryId).Select(c => c.DescendantId);
            q = q.Where(p => p.CategoryId != null && descendants.Contains(p.CategoryId.Value));
        }

        if (cursor is not null && long.TryParse(cursor.Value, out long ticks))
        {
            DateTime after = new DateTime(ticks, DateTimeKind.Utc);
            q = q.Where(p => p.PublishedAt < after || (p.PublishedAt == after && p.Id.CompareTo(cursor.Id) < 0));
        }

        List<Product> rows = await q.OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id).Take(limit + 1).ToListAsync(ct);
        bool more = rows.Count > limit;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        IReadOnlyList<ProductSummary> items = await SummariesAsync(rows, ct);
        string? next = more ? new Cursor("published_at", rows[^1].PublishedAt!.Value.Ticks.ToString(CultureInfo.InvariantCulture), rows[^1].Id).Encode() : null;
        return new PagedResponse<ProductSummary>(items, next);
    }

    public async Task<ProductDetail> GetProductAsync(Guid id, CancellationToken ct)
    {
        Product product = await LoadAsync(id, ct);
        TypeSchema schema = await _registry.GetAsync(product.ProductTypeId, product.TypeVersion, ct);
        List<ProductVariant> variants = await _db.ProductVariants.Where(v => v.ProductId == id).OrderBy(v => v.SortOrder).ThenBy(v => v.Sku).ToListAsync(ct);
        List<MediaAsset> media = await _db.Media.Where(m => m.ProductId == id).OrderBy(m => m.SortOrder).ToListAsync(ct);
        CategoryRef? category = await CategoryAsync(product.CategoryId, ct);
        return new ProductDetail(
            Map.Summary(product, schema, category),
            variants.Select(v => Map.VariantView(v, schema)).ToArray(),
            media.Select(Map.MediaView).ToArray(),
            product.Version);
    }

    public async Task<IReadOnlyList<OfferView>> GetOffersAsync(Guid productId, DateTime? asOf, CancellationToken ct)
    {
        await LoadAsync(productId, ct);
        DateTime at = Guards.Utc(asOf ?? Now);
        List<Guid> variantIds = await _db.ProductVariants.Where(v => v.ProductId == productId).Select(v => v.Id).ToListAsync(ct);
        List<Offer> offers = await _db.Offers
            .Where(o => o.Status == OfferStatus.Active && o.ValidFrom <= at && (o.ValidUntil == null || o.ValidUntil > at))
            .Where(o => o.ProductId == productId || (o.VariantId != null && variantIds.Contains(o.VariantId.Value)))
            .OrderBy(o => o.SortOrder).ThenBy(o => o.Name)
            .ToListAsync(ct);
        List<Guid> offerIds = offers.Select(o => o.Id).ToList();
        List<Price> prices = await _db.Prices
            .Where(p => offerIds.Contains(p.OfferId) && p.Status == PriceStatus.Active && p.ValidFrom <= at && (p.ValidUntil == null || p.ValidUntil > at))
            .ToListAsync(ct);
        return offers.Select(o => new OfferView(
            o.Id, o.Name, o.Description, o.BillingPeriod, o.ProductId, o.VariantId,
            prices.Where(p => p.OfferId == o.Id).OrderBy(p => p.Currency).ThenBy(p => p.PriceType)
                .Select(p => new PriceView(p.Amount, p.Currency, p.PriceType, p.ValidFrom, p.ValidUntil)).ToArray(),
            Json.Element(o.Eligibility), o.ValidFrom, o.ValidUntil)).ToArray();
    }

    public async Task<IReadOnlyList<AvailabilityView>> GetAvailabilityAsync(Guid productId, string? region, DateTime? asOf, CancellationToken ct)
    {
        await LoadAsync(productId, ct);
        if (region is not null)
        {
            Guards.RequireMatch(region, Guards.Region(), "region", "must be * or an ISO 3166-1 alpha-2 code.");
        }

        DateTime at = Guards.Utc(asOf ?? Now);
        List<Guid> variantIds = await _db.ProductVariants.Where(v => v.ProductId == productId).Select(v => v.Id).ToListAsync(ct);
        List<Availability> rows = await _db.Availability
            .Where(a => a.Status == AvailabilityStatus.Active && a.ValidFrom <= at && (a.ValidUntil == null || a.ValidUntil > at))
            .Where(a => a.ProductId == productId || (a.VariantId != null && variantIds.Contains(a.VariantId.Value)))
            .Where(a => region == null || a.Region == region || a.Region == "*")
            .OrderBy(a => a.Region)
            .ToListAsync(ct);
        return rows.Select(a => new AvailabilityView(a.ProductId, a.VariantId, a.Region, a.StockQty, a.Capacity, a.ValidFrom, a.ValidUntil)).ToArray();
    }

    public async Task<IReadOnlyList<RelatedProductView>> GetRelatedAsync(Guid productId, RelationshipType? type, CancellationToken ct)
    {
        await LoadAsync(productId, ct);
        List<ProductRelationship> outbound = await _db.ProductRelationships
            .Where(r => r.SourceId == productId && (type == null || r.RelationshipType == type))
            .OrderBy(r => r.SortOrder).ToListAsync(ct);
        RelationshipType[] symmetric = [RelationshipType.CompatibleWith, RelationshipType.AlternativeTo, RelationshipType.BundledWith];
        List<ProductRelationship> inbound = await _db.ProductRelationships
            .Where(r => r.TargetId == productId && symmetric.Contains(r.RelationshipType) && (type == null || r.RelationshipType == type))
            .OrderBy(r => r.SortOrder).ToListAsync(ct);

        List<Guid> ids = outbound.Select(r => r.TargetId).Concat(inbound.Select(r => r.SourceId)).Distinct().ToList();
        List<Product> products = await _db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        Dictionary<Guid, ProductSummary> summaries = (await SummariesAsync(products, ct)).ToDictionary(s => s.Id);

        List<RelatedProductView> result = new List<RelatedProductView>();
        foreach (ProductRelationship r in outbound)
        {
            if (summaries.TryGetValue(r.TargetId, out ProductSummary? s))
            {
                result.Add(new RelatedProductView(r.RelationshipType, "outbound", s, Json.Element(r.Metadata)));
            }
        }

        foreach (ProductRelationship r in inbound)
        {
            if (summaries.TryGetValue(r.SourceId, out ProductSummary? s))
            {
                result.Add(new RelatedProductView(r.RelationshipType, "inbound", s, Json.Element(r.Metadata)));
            }
        }

        return result;
    }

    public async Task<DocumentView> GetDocumentAsync(Guid productId, CancellationToken ct)
    {
        await LoadAsync(productId, ct);
        CatalogDocument doc = await _db.CatalogDocuments.FirstOrDefaultAsync(d => d.ProductId == productId && d.VariantId == null, ct)
            ?? throw new NotFoundException("Document", productId);
        return new DocumentView(doc.ProductId, doc.VariantId, doc.RenderedText, doc.ContentHash, doc.UpdatedAt);
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, int maxLimit, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        int limit = PageSize.Clamp(request.Limit, maxLimit, Math.Min(Limits.DefaultPageSize, maxLimit));
        string? query = NormalizeQuery(request.Query);
        string sortKey = ResolveSort(request.Sort, query is not null, out bool descending);
        Cursor? cursor = Cursor.Decode(request.Cursor, sortKey + (descending ? ":desc" : ":asc"));

        Guid? typeId = null;
        if (request.ProductType is not null)
        {
            Guards.RequireMatch(request.ProductType, Guards.Key(), "product_type", "must be a type key.");
            typeId = await _db.ProductTypes.Where(t => t.Key == request.ProductType).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct)
                ?? throw new ValidationException($"product_type '{request.ProductType}' does not exist.");
        }

        IReadOnlyDictionary<string, FilterableAttribute> filterable = await _registry.GetFilterableAsync(typeId, ct);
        CompiledFilters filters = FilterCompiler.Compile(request.Filters, filterable, IndexRelation, "p");

        List<NpgsqlParameter> parameters = filters.Parameters.Select(ToParameter).ToList();
        StringBuilder sql = new StringBuilder();
        string rankExpr = query is null ? "0::double precision" : "ts_rank(p.search_tsv, plainto_tsquery('simple', @q))";
        sql.Append("SELECT p.id AS \"Id\", ").Append(rankExpr).Append(" AS \"Rank\", p.name AS \"Name\", p.published_at AS \"PublishedAt\", p.created_at AS \"CreatedAt\" FROM catalog.v_products p WHERE 1=1");

        if (query is not null)
        {
            parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = query });
            sql.Append(" AND p.search_tsv @@ plainto_tsquery('simple', @q)");
        }

        if (request.CatalogId is { } catalogId)
        {
            parameters.Add(new NpgsqlParameter("catalog", NpgsqlDbType.Uuid) { Value = catalogId });
            sql.Append(" AND p.catalog_id = @catalog");
        }

        if (typeId is { } tid)
        {
            parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Uuid) { Value = tid });
            sql.Append(" AND p.product_type_id = @type");
        }

        if (request.Category is { } category)
        {
            parameters.Add(new NpgsqlParameter("category", NpgsqlDbType.Uuid) { Value = category });
            sql.Append(" AND p.category_id IS NOT NULL AND EXISTS (SELECT 1 FROM catalog.v_category_closure cc WHERE cc.ancestor_id = @category AND cc.descendant_id = p.category_id)");
        }

        foreach (string predicate in filters.Predicates)
        {
            sql.Append(" AND ").Append(predicate);
        }

        string sortColumn = sortKey switch
        {
            "relevance" => rankExpr,
            "name" => "p.name",
            "published_at" => "p.published_at",
            _ => "p.created_at",
        };
        string op = descending ? "<" : ">";
        if (cursor is not null)
        {
            AddCursorPredicate(sql, parameters, sortKey, sortColumn, op, cursor);
        }

        sql.Append(" ORDER BY ").Append(sortColumn).Append(descending ? " DESC" : " ASC").Append(", p.id ").Append(descending ? "DESC" : "ASC");
        parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit + 1 });
        sql.Append(" LIMIT @limit");

        List<SearchRow> rows = await _db.Database.SqlQueryRaw<SearchRow>(sql.ToString(), parameters.Cast<object>().ToArray()).ToListAsync(ct);
        bool more = rows.Count > limit;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        List<Guid> ids = rows.Select(r => r.Id).ToList();
        List<Product> products = await _db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        Dictionary<Guid, ProductSummary> summaries = (await SummariesAsync(products, ct)).ToDictionary(s => s.Id);

        List<SearchHit> hits = new List<SearchHit>(rows.Count);
        foreach (SearchRow row in rows)
        {
            if (!summaries.TryGetValue(row.Id, out ProductSummary? summary))
            {
                continue;
            }

            hits.Add(new SearchHit(summary, WhyMatched(summary, query, request.Category, filters.Explanations)));
        }

        string? next = null;
        if (more)
        {
            SearchRow last = rows[^1];
            string value = sortKey switch
            {
                "relevance" => last.Rank.ToString("R", CultureInfo.InvariantCulture),
                "name" => last.Name,
                "published_at" => (last.PublishedAt ?? DateTime.MinValue).Ticks.ToString(CultureInfo.InvariantCulture),
                _ => last.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture),
            };
            next = new Cursor(sortKey + (descending ? ":desc" : ":asc"), value, last.Id).Encode();
        }

        _metrics.SearchCompleted(sw.Elapsed.TotalMilliseconds, "read-api");
        return new SearchResponse(hits, next, Now);
    }

    public async Task<CompareResponse> CompareAsync(CompareRequest request, CancellationToken ct)
    {
        if (request.Ids is null || request.Ids.Count < 2 || request.Ids.Count > Limits.MaxCompareItems)
        {
            throw new ValidationException($"ids must contain 2 to {Limits.MaxCompareItems} product ids.");
        }

        List<Guid> ids = request.Ids.Distinct().ToList();
        List<Product> products = await _db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        if (products.Count != ids.Count)
        {
            throw new NotFoundException("One or more products were not found.");
        }

        products = ids.Select(id => products.First(p => p.Id == id)).ToList();
        Dictionary<(Guid, int), TypeSchema> schemas = new();
        foreach (Product p in products)
        {
            if (!schemas.ContainsKey((p.ProductTypeId, p.TypeVersion)))
            {
                schemas[(p.ProductTypeId, p.TypeVersion)] = await _registry.GetAsync(p.ProductTypeId, p.TypeVersion, ct);
            }
        }

        // Rows are aligned by registry key; label and unit come from the first type that defines the key.
        Dictionary<string, CompiledAttribute> defs = new Dictionary<string, CompiledAttribute>(StringComparer.Ordinal);
        foreach (TypeSchema schema in schemas.Values)
        {
            foreach (CompiledAttribute a in schema.For(AttributeScope.Product))
            {
                defs.TryAdd(a.Key, a);
            }
        }

        List<CompareRow> rows = new List<CompareRow>();
        foreach (CompiledAttribute def in defs.Values.OrderBy(d => d.DisplayGroup ?? string.Empty, StringComparer.Ordinal).ThenBy(d => d.SortOrder).ThenBy(d => d.Key, StringComparer.Ordinal))
        {
            Dictionary<Guid, JsonElement?> values = new Dictionary<Guid, JsonElement?>();
            foreach (Product p in products)
            {
                using JsonDocument doc = JsonDocument.Parse(p.Attributes);
                values[p.Id] = doc.RootElement.TryGetProperty(def.Key, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? v.Clone() : null;
            }

            rows.Add(new CompareRow(def.Key, def.Label, def.Unit, def.DataType, def.DisplayGroup, values));
        }

        IReadOnlyList<ProductSummary> summaries = await SummariesAsync(products, ct);
        return new CompareResponse(summaries, rows, Now);
    }

    private static IReadOnlyList<string> WhyMatched(ProductSummary summary, string? query, Guid? category, IReadOnlyList<string> filterExplanations)
    {
        List<string> why = new List<string>();
        if (query is not null)
        {
            string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] fields = [summary.Name, summary.Sku, summary.Description ?? string.Empty];
            string[] names = ["name", "sku", "description"];
            for (int i = 0; i < fields.Length; i++)
            {
                string[] hitTerms = terms.Where(t => fields[i].Contains(t, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (hitTerms.Length > 0)
                {
                    why.Add($"{names[i]} matches \"{string.Join(" ", hitTerms)}\"");
                }
            }

            if (why.Count == 0)
            {
                why.Add($"full-text match for \"{query}\"");
            }
        }

        if (category is not null && summary.Category is not null)
        {
            why.Add($"in category {string.Join(" > ", summary.Category.Path)}");
        }

        why.AddRange(filterExplanations.Select(e => $"filter {e}"));
        return why;
    }

    private static void AddCursorPredicate(StringBuilder sql, List<NpgsqlParameter> parameters, string sortKey, string sortColumn, string op, Cursor cursor)
    {
        parameters.Add(new NpgsqlParameter("cid", NpgsqlDbType.Uuid) { Value = cursor.Id });
        switch (sortKey)
        {
            case "relevance":
                parameters.Add(new NpgsqlParameter("cv", NpgsqlDbType.Double) { Value = double.Parse(cursor.Value ?? "0", CultureInfo.InvariantCulture) });
                break;
            case "name":
                parameters.Add(new NpgsqlParameter("cv", NpgsqlDbType.Text) { Value = cursor.Value ?? string.Empty });
                break;
            default:
                if (!long.TryParse(cursor.Value, out long ticks))
                {
                    throw new ValidationException("cursor is not valid.");
                }

                parameters.Add(new NpgsqlParameter("cv", NpgsqlDbType.TimestampTz) { Value = new DateTime(ticks, DateTimeKind.Utc) });
                break;
        }

        sql.Append(" AND (").Append(sortColumn).Append(' ').Append(op).Append(" @cv OR (").Append(sortColumn).Append(" = @cv AND p.id ").Append(op).Append(" @cid))");
    }

    private static string ResolveSort(string? sort, bool hasQuery, out bool descending)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            descending = true;
            return hasQuery ? "relevance" : "published_at";
        }

        descending = sort.StartsWith('-');
        string key = descending ? sort[1..] : sort;
        if (!Sorts.Contains(key, StringComparer.Ordinal))
        {
            throw new ValidationException($"sort must be one of: {string.Join(", ", Sorts)} (prefix with - for descending).");
        }

        if (key == "relevance")
        {
            if (!hasQuery)
            {
                throw new ValidationException("sort=relevance requires a query.");
            }

            descending = !sort.StartsWith('+');
        }

        return key;
    }

    private static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        if (query.Length > Limits.MaxQueryLength)
        {
            throw new ValidationException($"query must be at most {Limits.MaxQueryLength} characters.");
        }

        if (Guards.HasControlCharacters(query))
        {
            throw new ValidationException("query must not contain control characters.");
        }

        return query.Trim();
    }

    private static NpgsqlParameter ToParameter(SqlParameterSpec spec) => spec.Kind switch
    {
        SqlParamKind.Uuid => new NpgsqlParameter(spec.Name, NpgsqlDbType.Uuid) { Value = spec.Value },
        SqlParamKind.Text => new NpgsqlParameter(spec.Name, NpgsqlDbType.Text) { Value = spec.Value },
        SqlParamKind.Numeric => new NpgsqlParameter(spec.Name, NpgsqlDbType.Numeric) { Value = spec.Value },
        SqlParamKind.Boolean => new NpgsqlParameter(spec.Name, NpgsqlDbType.Boolean) { Value = spec.Value },
        SqlParamKind.TimestampUtc => new NpgsqlParameter(spec.Name, NpgsqlDbType.TimestampTz) { Value = spec.Value },
        SqlParamKind.TextArray => new NpgsqlParameter(spec.Name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = spec.Value },
        SqlParamKind.NumericArray => new NpgsqlParameter(spec.Name, NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = spec.Value },
        SqlParamKind.Integer => new NpgsqlParameter(spec.Name, NpgsqlDbType.Integer) { Value = spec.Value },
        _ => throw new ArgumentOutOfRangeException(nameof(spec)),
    };

    private async Task<IReadOnlyList<ProductSummary>> SummariesAsync(IReadOnlyList<Product> products, CancellationToken ct)
    {
        Dictionary<(Guid, int), TypeSchema> schemas = new();
        Dictionary<Guid, CategoryRef?> categories = new();
        List<ProductSummary> result = new List<ProductSummary>(products.Count);
        foreach (Product p in products)
        {
            if (!schemas.TryGetValue((p.ProductTypeId, p.TypeVersion), out TypeSchema? schema))
            {
                schema = await _registry.GetAsync(p.ProductTypeId, p.TypeVersion, ct);
                schemas[(p.ProductTypeId, p.TypeVersion)] = schema;
            }

            CategoryRef? category = null;
            if (p.CategoryId is { } cid)
            {
                if (!categories.TryGetValue(cid, out category))
                {
                    category = await CategoryAsync(cid, ct);
                    categories[cid] = category;
                }
            }

            result.Add(Map.Summary(p, schema, category));
        }

        return result;
    }

    private async Task<CategoryRef?> CategoryAsync(Guid? categoryId, CancellationToken ct)
    {
        if (categoryId is null)
        {
            return null;
        }

        Category? category = await _db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null)
        {
            return null;
        }

        List<(string Name, int Depth)> path = await _db.CategoryClosure.Where(c => c.DescendantId == categoryId)
            .Join(_db.Categories, c => c.AncestorId, cat => cat.Id, (c, cat) => new ValueTuple<string, int>(cat.Name, c.Depth))
            .ToListAsync(ct);
        return new CategoryRef(category.Id, category.Key, category.Name, path.OrderByDescending(x => x.Depth).Select(x => x.Name).ToArray());
    }

    private async Task<Product> LoadAsync(Guid id, CancellationToken ct)
        => await _db.Products.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Product", id);

    private sealed class SearchRow
    {
        public Guid Id { get; set; }

        public double Rank { get; set; }

        public string Name { get; set; } = string.Empty;

        public DateTime? PublishedAt { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
