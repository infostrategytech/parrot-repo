using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Parrot.Catalog.Application.Documents;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>
/// Rebuilds everything derived from a product's truth inside the caller's transaction:
/// the typed attribute index, the search text, the canonical document (when published)
/// and the immutable version snapshot (ADR-002).
/// </summary>
public sealed class DerivedDataRebuilder
{
    private readonly CatalogDbContext _db;
    private readonly ICanonicalDocumentRenderer _renderer;
    private readonly TimeProvider _clock;
    private readonly CatalogMetrics _metrics;
    private readonly IWriteSchemaRegistry _registry;

    public DerivedDataRebuilder(CatalogDbContext db, ICanonicalDocumentRenderer renderer, TimeProvider clock, CatalogMetrics metrics, IWriteSchemaRegistry registry)
    {
        _db = db;
        _renderer = renderer;
        _clock = clock;
        _metrics = metrics;
        _registry = registry;
    }

    /// <summary>Offers, prices, availability and relationships are part of the document; after they change, the published product's document is re-rendered.</summary>
    public async Task RerenderIfPublishedAsync(Guid productId, CancellationToken ct)
    {
        Product? product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.DeletedAt == null, ct);
        if (product is null || product.Status != ProductStatus.Published)
        {
            return;
        }

        TypeSchema schema = await _registry.GetAsync(product.ProductTypeId, product.TypeVersion, ct);
        await RenderDocumentAsync(product, schema, ct);
    }

    public async Task RebuildAsync(Product product, TypeSchema schema, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        List<ProductVariant> variants = await ActiveVariantsAsync(product.Id, ct);

        await _db.ProductAttributeIndex.Where(i => i.ProductId == product.Id).ExecuteDeleteAsync(ct);
        _db.ProductAttributeIndex.AddRange(DerivedIndexBuilder.BuildRows(schema, product.TenantId, product.CatalogId, product.Id, null, product.Attributes));
        foreach (ProductVariant v in variants)
        {
            _db.ProductAttributeIndex.AddRange(DerivedIndexBuilder.BuildRows(schema, product.TenantId, product.CatalogId, product.Id, v.Id, v.Attributes));
        }

        product.SearchText = DerivedIndexBuilder.BuildSearchText(schema, product.Name, product.Sku, product.Description, product.Attributes, variants.Select(v => v.Attributes));

        if (product.Status == ProductStatus.Published)
        {
            await RenderDocumentAsync(product, schema, variants, ct);
        }

        _metrics.IndexRebuilt(sw.Elapsed.TotalMilliseconds);
    }

    public async Task RenderDocumentAsync(Product product, TypeSchema schema, CancellationToken ct)
        => await RenderDocumentAsync(product, schema, await ActiveVariantsAsync(product.Id, ct), ct);

    /// <summary>
    /// A document lists the published products its product points to. When a target is
    /// published or retired, every published source that points to it is re-rendered.
    /// </summary>
    public async Task RerenderSourcesOfAsync(Guid targetId, CancellationToken ct)
    {
        List<Guid> sourceIds = await _db.ProductRelationships.AsNoTracking()
            .Where(r => r.TargetId == targetId && r.DeletedAt == null)
            .Select(r => r.SourceId)
            .Distinct()
            .ToListAsync(ct);
        foreach (Guid sourceId in sourceIds)
        {
            await RerenderIfPublishedAsync(sourceId, ct);
        }
    }

    public async Task SnapshotAsync(Product product, TypeSchema schema, string actor, CancellationToken ct)
    {
        List<ProductVariant> variants = await ActiveVariantsAsync(product.Id, ct);
        (List<Offer> offers, List<Price> prices) = await OffersAsync(product.Id, variants.Select(v => v.Id).ToList(), includeInactive: true, ct);
        List<Availability> availability = await AvailabilityAsync(product.Id, variants.Select(v => v.Id).ToList(), ct);
        List<ProductRelationship> relationships = await _db.ProductRelationships.AsNoTracking()
            .Where(r => r.SourceId == product.Id && r.DeletedAt == null).ToListAsync(ct);

        product.Version += 1;
        object snapshot = new
        {
            product = new
            {
                product.Id, product.CatalogId, product.ProductTypeId, product.TypeVersion, product.CategoryId, product.Sku, product.Name, product.Slug,
                product.Description, product.Status, Attributes = Json.Element(product.Attributes), product.Version, product.PublishedAt,
            },
            type = new { schema.TypeKey, schema.TypeName, schema.Kind, schema.TypeVersion },
            variants = variants.Select(v => new { v.Id, v.Sku, v.Name, v.Status, v.SortOrder, Attributes = Json.Element(v.Attributes) }),
            offers = offers.Select(o => new
            {
                o.Id, o.ProductId, o.VariantId, o.Name, o.Description, o.BillingPeriod, o.Status, Eligibility = Json.Element(o.Eligibility), o.ValidFrom, o.ValidUntil,
                prices = prices.Where(p => p.OfferId == o.Id).Select(p => new { p.Id, p.Amount, p.Currency, p.PriceType, p.Status, p.ValidFrom, p.ValidUntil }),
            }),
            availability = availability.Select(a => new { a.Id, a.ProductId, a.VariantId, a.Region, a.StockQty, a.Capacity, a.Status, a.ValidFrom, a.ValidUntil }),
            relationships = relationships.Select(r => new { r.Id, r.TargetId, r.RelationshipType, Metadata = Json.Element(r.Metadata) }),
        };
        string json = Json.Serialize(snapshot);
        _db.ProductVersions.Add(new ProductVersion
        {
            Id = Guid.NewGuid(),
            TenantId = product.TenantId,
            CatalogId = product.CatalogId,
            ProductId = product.Id,
            VersionNumber = product.Version,
            Snapshot = json,
            ContentHash = CanonicalDocumentRenderer.Hash(json),
            PublishedBy = actor,
            PublishedAt = _clock.GetUtcNow().UtcDateTime,
        });
    }

    public async Task<IReadOnlyList<string>> CategoryPathAsync(Guid? categoryId, CancellationToken ct)
    {
        if (categoryId is null)
        {
            return [];
        }

        List<(string Name, int Depth)> rows = await _db.CategoryClosure.AsNoTracking()
            .Where(c => c.DescendantId == categoryId)
            .Join(_db.Categories.AsNoTracking(), c => c.AncestorId, cat => cat.Id, (c, cat) => new ValueTuple<string, int>(cat.Name, c.Depth))
            .ToListAsync(ct);
        return rows.OrderByDescending(r => r.Depth).Select(r => r.Name).ToArray();
    }

    private async Task RenderDocumentAsync(Product product, TypeSchema schema, List<ProductVariant> variants, CancellationToken ct)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        List<Guid> variantIds = variants.Select(v => v.Id).ToList();
        (List<Offer> offers, List<Price> prices) = await OffersAsync(product.Id, variantIds, includeInactive: false, ct);
        List<Availability> availability = (await AvailabilityAsync(product.Id, variantIds, ct))
            .Where(a => a.Status == AvailabilityStatus.Active && a.ValidFrom <= now && (a.ValidUntil == null || a.ValidUntil > now)).ToList();
        List<(RelationshipType, string, string)> related = await _db.ProductRelationships.AsNoTracking()
            .Where(r => r.SourceId == product.Id && r.DeletedAt == null)
            .Join(_db.Products.AsNoTracking().Where(p => p.Status == ProductStatus.Published && p.DeletedAt == null), r => r.TargetId, p => p.Id, (r, p) => new ValueTuple<RelationshipType, string, string>(r.RelationshipType, p.Name, p.Sku))
            .ToListAsync(ct);

        RenderInput input = new RenderInput(
            product,
            schema,
            await CategoryPathAsync(product.CategoryId, ct),
            variants,
            offers.Where(o => o.Status == OfferStatus.Active && o.ValidFrom <= now && (o.ValidUntil == null || o.ValidUntil > now))
                .Select(o => (o, (IReadOnlyList<Price>)prices.Where(p => p.OfferId == o.Id && p.Status == PriceStatus.Active && p.ValidFrom <= now && (p.ValidUntil == null || p.ValidUntil > now)).ToList()))
                .ToList(),
            availability,
            related,
            now);

        RenderedDocument rendered = _renderer.Render(input);
        CatalogDocument? existing = await _db.CatalogDocuments.FirstOrDefaultAsync(d => d.ProductId == product.Id && d.VariantId == null, ct);
        if (existing is null)
        {
            _db.CatalogDocuments.Add(new CatalogDocument
            {
                Id = Guid.NewGuid(),
                TenantId = product.TenantId,
                CatalogId = product.CatalogId,
                ProductId = product.Id,
                VariantId = null,
                RenderedText = rendered.Text,
                ContentHash = rendered.ContentHash,
                UpdatedAt = now,
            });
        }
        else if (!string.Equals(existing.ContentHash, rendered.ContentHash, StringComparison.Ordinal))
        {
            existing.RenderedText = rendered.Text;
            existing.ContentHash = rendered.ContentHash;
            existing.UpdatedAt = now;
        }
    }

    private Task<List<ProductVariant>> ActiveVariantsAsync(Guid productId, CancellationToken ct)
        => _db.ProductVariants.AsNoTracking()
            .Where(v => v.ProductId == productId && v.DeletedAt == null && v.Status == VariantStatus.Active)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Sku)
            .ToListAsync(ct);

    private async Task<(List<Offer>, List<Price>)> OffersAsync(Guid productId, List<Guid> variantIds, bool includeInactive, CancellationToken ct)
    {
        List<Offer> offers = await _db.Offers.AsNoTracking()
            .Where(o => o.DeletedAt == null && (o.ProductId == productId || (o.VariantId != null && variantIds.Contains(o.VariantId.Value))))
            .Where(o => includeInactive || o.Status != OfferStatus.Draft)
            .ToListAsync(ct);
        List<Guid> offerIds = offers.Select(o => o.Id).ToList();
        List<Price> prices = await _db.Prices.AsNoTracking()
            .Where(p => p.DeletedAt == null && offerIds.Contains(p.OfferId))
            .ToListAsync(ct);
        return (offers, prices);
    }

    private Task<List<Availability>> AvailabilityAsync(Guid productId, List<Guid> variantIds, CancellationToken ct)
        => _db.Availability.AsNoTracking()
            .Where(a => a.DeletedAt == null && (a.ProductId == productId || (a.VariantId != null && variantIds.Contains(a.VariantId.Value))))
            .ToListAsync(ct);
}
