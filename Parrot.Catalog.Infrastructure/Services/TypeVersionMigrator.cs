using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Parrot.Catalog.Application.Audit;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Events;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>
/// Moves products from one type version to another. Keys the new version no longer
/// defines are dropped; everything else is validated against the new schema. Dry run
/// reports and changes nothing; a real run migrates the valid products in one
/// transaction and reports the invalid ones untouched.
/// </summary>
public sealed class TypeVersionMigrator : AdminServiceBase, ITypeVersionMigrator
{
    private readonly IWriteSchemaRegistry _registry;
    private readonly DerivedDataRebuilder _rebuilder;

    public TypeVersionMigrator(CatalogDbContext db, ITenantContext tenant, TimeProvider clock, CatalogMetrics metrics, ItemEventWriter itemEvents, IWriteSchemaRegistry registry, DerivedDataRebuilder rebuilder)
        : base(db, tenant, clock, metrics, itemEvents)
    {
        _registry = registry;
        _rebuilder = rebuilder;
    }

    public async Task<TypeVersionMigrationReport> MigrateAsync(Guid typeId, int fromVersion, int toVersion, bool dryRun, CancellationToken ct)
    {
        if (fromVersion == toVersion)
        {
            throw new ValidationException("from and to versions must differ.");
        }

        TypeSchema target = await _registry.GetAsync(typeId, toVersion, ct);
        List<Product> products = await Db.Products
            .Where(p => p.ProductTypeId == typeId && p.TypeVersion == fromVersion && p.DeletedAt == null)
            .OrderBy(p => p.Sku)
            .ToListAsync(ct);
        List<Guid> ids = products.Select(p => p.Id).ToList();
        List<ProductVariant> variants = await Db.ProductVariants
            .Where(v => ids.Contains(v.ProductId) && v.DeletedAt == null)
            .ToListAsync(ct);

        List<TypeVersionMigrationItem> items = new List<TypeVersionMigrationItem>();
        List<(Product Product, string Attributes, List<(ProductVariant Variant, string Attributes)> Variants)> migratable = new();

        foreach (Product product in products)
        {
            (string attrs, List<string> dropped) = Filter(product.Attributes, target, AttributeScope.Product);
            List<string> errors = target.Validate(attrs, AttributeScope.Product).ToList();
            List<(ProductVariant, string)> migratedVariants = new List<(ProductVariant, string)>();
            foreach (ProductVariant v in variants.Where(v => v.ProductId == product.Id))
            {
                (string vattrs, List<string> vdropped) = Filter(v.Attributes, target, AttributeScope.Variant);
                dropped.AddRange(vdropped.Select(k => $"{v.Sku}:{k}"));
                errors.AddRange(target.Validate(vattrs, AttributeScope.Variant).Select(e => $"variant {v.Sku}: {e}"));
                migratedVariants.Add((v, vattrs));
            }

            bool valid = errors.Count == 0;
            items.Add(new TypeVersionMigrationItem(product.Id, product.Sku, valid, errors, dropped));
            if (valid)
            {
                migratable.Add((product, attrs, migratedVariants));
            }
        }

        if (!dryRun && migratable.Count > 0)
        {
            await WriteAsync(async c =>
            {
                foreach ((Product product, string attrs, List<(ProductVariant Variant, string Attributes)> vs) in migratable)
                {
                    product.Attributes = attrs;
                    product.TypeVersion = toVersion;
                    product.UpdatedBy = Tenant.Actor;
                    Stamp(product, created: false);
                    foreach ((ProductVariant variant, string vattrs) in vs)
                    {
                        variant.Attributes = vattrs;
                        Stamp(variant, created: false);
                    }

                    await SaveAsync(c);
                    await _rebuilder.RebuildAsync(product, target, c);
                    if (product.Status == ProductStatus.Published)
                    {
                        await _rebuilder.SnapshotAsync(product, target, Tenant.Actor, c);
                    }

                    Audit(AuditActions.TypeVersionMigrated, "product", product.Id, new { type_version = fromVersion }, new { type_version = toVersion });
                    Enqueue(AggregateTypes.Product, product.Id, CatalogEventTypes.ProductUpdated, new { product_id = product.Id, type_version = toVersion, reason = "type_version_migrated" });
                }
            }, ct);
        }

        return new TypeVersionMigrationReport(typeId, fromVersion, toVersion, dryRun, items.Count, dryRun ? 0 : migratable.Count, items.Count(i => !i.Valid), items);
    }

    private static (string Attributes, List<string> Dropped) Filter(string attributesJson, TypeSchema target, AttributeScope scope)
    {
        JsonObject source = JsonNode.Parse(attributesJson) as JsonObject ?? new JsonObject();
        JsonObject result = new JsonObject();
        List<string> dropped = new List<string>();
        foreach ((string key, JsonNode? value) in source)
        {
            CompiledAttribute? def = target.Find(key);
            if (def is null || def.VariantAxis != (scope == AttributeScope.Variant))
            {
                dropped.Add(key);
                continue;
            }

            result[key] = value?.DeepClone();
        }

        return (result.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), dropped);
    }
}
