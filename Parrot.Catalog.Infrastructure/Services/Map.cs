using System.Text.Json;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Domain.Entities;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>Entity to contract mapping. Internal fields (tenant_id, search_text, actors on read DTOs) never leave here.</summary>
internal static class Map
{
    public static CatalogResponse Catalog(CatalogEntity c)
        => new(c.Id, c.Key, c.Name, c.DefaultCurrency, c.Status, c.CreatedAt, c.UpdatedAt);

    public static AttributeDefinitionResponse Definition(AttributeDefinition d)
    {
        CompiledAttribute compiled = CompiledAttribute.From(d);
        return new AttributeDefinitionResponse(
            d.Id, d.TypeVersion, d.Key, d.Label, d.DataType, d.Unit, d.Required, compiled.EnumOptions,
            d.Filterable, d.Searchable, d.Embeddable, d.VariantAxis, d.DisplayGroup, d.SortOrder, Json.ElementOrNull(d.Validation));
    }

    public static ProductTypeResponse Type(ProductType t, IEnumerable<AttributeDefinition> definitions)
        => new(t.Id, t.Key, t.Name, t.Kind, t.Version, t.Status,
            definitions.OrderBy(d => d.SortOrder).ThenBy(d => d.Key, StringComparer.Ordinal).Select(Definition).ToArray(),
            t.CreatedAt, t.UpdatedAt);

    public static CategoryResponse Category(Category c, IReadOnlyList<string> path)
        => new(c.Id, c.CatalogId, c.ParentId, c.Key, c.Name, c.Slug, c.Description, c.SortOrder, path, c.CreatedAt, c.UpdatedAt);

    public static VariantResponse Variant(ProductVariant v)
        => new(v.Id, v.ProductId, v.Sku, v.Name, Json.Element(v.Attributes), v.Status, v.SortOrder, v.CreatedAt, v.UpdatedAt);

    public static TypeRef TypeRef(ProductType t, int pinnedVersion) => new(t.Id, t.Key, t.Name, t.Kind, pinnedVersion);

    public static TypeRef TypeRef(TypeSchema s) => new(s.ProductTypeId, s.TypeKey, s.TypeName, s.Kind, s.TypeVersion);

    public static ProductAdminResponse ProductAdmin(Product p, ProductType type, IReadOnlyList<ProductVariant> variants)
        => new(p.Id, p.CatalogId, TypeRef(type, p.TypeVersion), p.CategoryId, p.Sku, p.Name, p.Slug, p.Description, p.Status,
            Json.Element(p.Attributes), p.Version, p.PublishedAt, p.RetiredAt, p.CreatedBy, p.UpdatedBy, p.CreatedAt, p.UpdatedAt,
            variants.OrderBy(v => v.SortOrder).ThenBy(v => v.Sku, StringComparer.Ordinal).Select(Variant).ToArray());

    public static PriceResponse Price(Price p)
        => new(p.Id, p.OfferId, p.Amount, p.Currency, p.PriceType, p.Status, p.ValidFrom, p.ValidUntil, p.CreatedAt, p.UpdatedAt);

    public static OfferAdminResponse Offer(Offer o, IEnumerable<Price> prices)
        => new(o.Id, o.CatalogId, o.ProductId, o.VariantId, o.Name, o.Description, o.BillingPeriod, o.Status, Json.Element(o.Eligibility),
            o.ValidFrom, o.ValidUntil, o.SortOrder, prices.OrderBy(p => p.Currency, StringComparer.Ordinal).ThenBy(p => p.ValidFrom).Select(Price).ToArray(),
            o.CreatedAt, o.UpdatedAt);

    public static AvailabilityResponse Availability(Availability a)
        => new(a.Id, a.ProductId, a.VariantId, a.Region, a.StockQty, a.Capacity, a.Status, a.ValidFrom, a.ValidUntil, a.CreatedAt, a.UpdatedAt);

    public static RelationshipResponse Relationship(ProductRelationship r)
        => new(r.Id, r.SourceId, r.TargetId, r.RelationshipType, Json.Element(r.Metadata), r.SortOrder, r.CreatedAt);

    public static MediaResponse Media(MediaAsset m)
        => new(m.Id, m.ProductId, m.VariantId, m.Kind, m.S3Key, m.MimeType, m.AltText, m.Title, m.SizeBytes, m.SortOrder, m.CreatedAt);

    public static IReadOnlyList<AttributeValue> Attributes(TypeSchema schema, string attributesJson, AttributeScope scope)
    {
        List<AttributeValue> values = new List<AttributeValue>();
        using JsonDocument doc = JsonDocument.Parse(attributesJson);
        foreach (CompiledAttribute def in schema.For(scope))
        {
            if (doc.RootElement.TryGetProperty(def.Key, out JsonElement value) && value.ValueKind != JsonValueKind.Null)
            {
                values.Add(new AttributeValue(def.Key, def.Label, def.Unit, def.DataType, def.DisplayGroup, value.Clone()));
            }
        }

        return values;
    }

    public static ProductSummary Summary(Product p, TypeSchema schema, CategoryRef? category)
        => new(p.Id, p.Sku, p.Name, p.Slug, p.Description, TypeRef(schema), category, Attributes(schema, p.Attributes, AttributeScope.Product), p.PublishedAt, p.UpdatedAt);

    public static VariantView VariantView(ProductVariant v, TypeSchema schema)
        => new(v.Id, v.Sku, v.Name, Attributes(schema, v.Attributes, AttributeScope.Variant), v.SortOrder);

    public static MediaView MediaView(MediaAsset m)
        => new(m.Id, m.Kind, m.S3Key, m.MimeType, m.AltText, m.Title, m.SortOrder);
}
