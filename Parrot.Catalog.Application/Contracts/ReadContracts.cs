using System.ComponentModel;
using System.Text.Json;
using Parrot.Catalog.Application.Filters;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Application.Contracts;

public sealed record ProductSummary(
    Guid Id,
    string Sku,
    string Name,
    string Slug,
    string? Description,
    TypeRef Type,
    CategoryRef? Category,
    IReadOnlyList<AttributeValue> Attributes,
    DateTime? PublishedAt,
    DateTime UpdatedAt);

public sealed record VariantView(Guid Id, string Sku, string Name, IReadOnlyList<AttributeValue> Attributes, int SortOrder);

public sealed record MediaView(Guid Id, MediaKind Kind, string S3Key, string MimeType, string? AltText, string? Title, int SortOrder);

public sealed record ProductDetail(
    ProductSummary Product,
    IReadOnlyList<VariantView> Variants,
    IReadOnlyList<MediaView> Media,
    int Version);

public sealed record PriceView(decimal Amount, string Currency, PriceType PriceType, DateTime ValidFrom, DateTime? ValidUntil);

public sealed record OfferView(
    Guid Id,
    string Name,
    string? Description,
    BillingPeriod BillingPeriod,
    Guid? ProductId,
    Guid? VariantId,
    string? Unit,
    bool PriceOnRequest,
    IReadOnlyList<PriceView> Prices,
    JsonElement Eligibility,
    DateTime ValidFrom,
    DateTime? ValidUntil);

public sealed record AvailabilityView(Guid? ProductId, Guid? VariantId, string Region, int? StockQty, int? Capacity, AvailabilityMode Mode, int? LeadTimeDays, DateTime ValidFrom, DateTime? ValidUntil);

public sealed record RelatedProductView(RelationshipType RelationshipType, string Direction, ProductSummary Product, JsonElement Metadata);

public sealed record DocumentView(Guid ProductId, Guid? VariantId, string Text, string ContentHash, DateTime UpdatedAt);

public sealed record ReadListQuery(Guid? CatalogId, Guid? CategoryId, Guid? ProductTypeId, string? Cursor, int? Limit);

[Description("Deterministic search. Filter keys must be filterable attributes in the registry; everything else is a 400.")]
public sealed record SearchRequest(
    string? Query = null,
    Guid? Category = null,
    Guid? CatalogId = null,
    [property: Description("A product type key; narrows results and disambiguates attribute keys shared across types.")] string? ProductType = null,
    IReadOnlyDictionary<string, FilterClause>? Filters = null,
    [property: Description("relevance (default with a query), name, published_at, created_at; prefix with - for descending.")] string? Sort = null,
    string? Cursor = null,
    int? Limit = null);

public sealed record SearchHit(ProductSummary Product, IReadOnlyList<string> WhyMatched);

public sealed record SearchResponse(IReadOnlyList<SearchHit> Items, string? NextCursor, DateTime DataAsOf);

public sealed record CompareRequest(IReadOnlyList<Guid> Ids);

public sealed record CompareRow(string Key, string Label, string? Unit, AttributeDataType DataType, string? DisplayGroup, IReadOnlyDictionary<Guid, JsonElement?> Values);

public sealed record CompareResponse(IReadOnlyList<ProductSummary> Products, IReadOnlyList<CompareRow> Rows, DateTime DataAsOf);
