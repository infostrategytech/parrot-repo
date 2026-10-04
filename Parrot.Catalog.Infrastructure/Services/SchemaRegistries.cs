using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>The registry as seen through the writer connection (base tables).</summary>
public interface IWriteSchemaRegistry : ISchemaRegistry
{
}

/// <summary>The registry as seen through the reader connection (v_* views).</summary>
public interface IReadSchemaRegistry : ISchemaRegistry
{
}

/// <summary>
/// Compiles attribute definitions into a <see cref="TypeSchema"/> once per
/// (tenant, type, version) and keeps it for a minute. In-process invalidation bumps a
/// per-tenant generation; the TTL covers other instances.
/// </summary>
public abstract class SchemaRegistryBase : ISchemaRegistry
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<Guid, int> Generations = new();

    private readonly IMemoryCache _cache;
    private readonly ITenantContext _tenant;

    protected SchemaRegistryBase(IMemoryCache cache, ITenantContext tenant)
    {
        _cache = cache;
        _tenant = tenant;
    }

    public async Task<TypeSchema> GetAsync(Guid productTypeId, int typeVersion, CancellationToken ct)
    {
        Guid tenantId = _tenant.TenantId;
        string key = $"catalog:schema:{tenantId:N}:{Generation(tenantId)}:{productTypeId:N}:{typeVersion}";
        if (_cache.TryGetValue(key, out TypeSchema? cached) && cached is not null)
        {
            return cached;
        }

        ProductType type = await LoadTypeAsync(productTypeId, ct) ?? throw new NotFoundException("Product type", productTypeId);
        if (typeVersion < 1 || typeVersion > type.Version)
        {
            throw new ValidationException($"Type version {typeVersion} does not exist for type '{type.Key}' (latest is {type.Version}).");
        }

        List<AttributeDefinition> definitions = await LoadDefinitionsAsync(productTypeId, typeVersion, ct);
        TypeSchema schema = new TypeSchema(type, definitions);
        _cache.Set(key, schema, Ttl);
        return schema;
    }

    public async Task<IReadOnlyDictionary<string, FilterableAttribute>> GetFilterableAsync(Guid? productTypeId, CancellationToken ct)
    {
        Guid tenantId = _tenant.TenantId;
        string key = $"catalog:filterable:{tenantId:N}:{Generation(tenantId)}:{productTypeId?.ToString("N") ?? "all"}";
        if (_cache.TryGetValue(key, out IReadOnlyDictionary<string, FilterableAttribute>? cached) && cached is not null)
        {
            return cached;
        }

        List<AttributeDefinition> definitions = await LoadFilterableAsync(productTypeId, ct);
        Dictionary<string, FilterableAttribute> result = new Dictionary<string, FilterableAttribute>(StringComparer.Ordinal);
        foreach (IGrouping<string, AttributeDefinition> group in definitions.GroupBy(d => d.Key, StringComparer.Ordinal))
        {
            AttributeDefinition first = group.OrderByDescending(d => d.TypeVersion).First();
            CompiledAttribute compiled = CompiledAttribute.From(first);
            bool ambiguous = group.Select(d => CompiledAttribute.From(d).IndexColumn).Distinct().Count() > 1;
            result[group.Key] = new FilterableAttribute(group.Key, first.DataType, compiled.IndexColumn, first.Label, first.Unit, ambiguous);
        }

        _cache.Set(key, result, Ttl);
        return result;
    }

    public void Invalidate(Guid productTypeId)
    {
        if (_tenant.IsSet)
        {
            Generations.AddOrUpdate(_tenant.TenantId, 1, (_, g) => g + 1);
        }
    }

    private static int Generation(Guid tenantId) => Generations.GetValueOrDefault(tenantId);

    protected abstract Task<ProductType?> LoadTypeAsync(Guid id, CancellationToken ct);

    protected abstract Task<List<AttributeDefinition>> LoadDefinitionsAsync(Guid typeId, int version, CancellationToken ct);

    protected abstract Task<List<AttributeDefinition>> LoadFilterableAsync(Guid? typeId, CancellationToken ct);
}

public sealed class WriteSchemaRegistry : SchemaRegistryBase, IWriteSchemaRegistry
{
    private readonly CatalogDbContext _db;

    public WriteSchemaRegistry(CatalogDbContext db, IMemoryCache cache, ITenantContext tenant)
        : base(cache, tenant)
    {
        _db = db;
    }

    protected override Task<ProductType?> LoadTypeAsync(Guid id, CancellationToken ct)
        => _db.ProductTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    protected override Task<List<AttributeDefinition>> LoadDefinitionsAsync(Guid typeId, int version, CancellationToken ct)
        => _db.AttributeDefinitions.AsNoTracking().Where(d => d.ProductTypeId == typeId && d.TypeVersion == version).ToListAsync(ct);

    protected override Task<List<AttributeDefinition>> LoadFilterableAsync(Guid? typeId, CancellationToken ct)
        => _db.AttributeDefinitions.AsNoTracking()
            .Where(d => d.Filterable && (typeId == null || d.ProductTypeId == typeId))
            .ToListAsync(ct);
}

public sealed class ReadSchemaRegistry : SchemaRegistryBase, IReadSchemaRegistry
{
    private readonly CatalogReadDbContext _db;

    public ReadSchemaRegistry(CatalogReadDbContext db, IMemoryCache cache, ITenantContext tenant)
        : base(cache, tenant)
    {
        _db = db;
    }

    protected override Task<ProductType?> LoadTypeAsync(Guid id, CancellationToken ct)
        => _db.ProductTypes.FirstOrDefaultAsync(t => t.Id == id, ct);

    protected override Task<List<AttributeDefinition>> LoadDefinitionsAsync(Guid typeId, int version, CancellationToken ct)
        => _db.AttributeDefinitions.Where(d => d.ProductTypeId == typeId && d.TypeVersion == version).ToListAsync(ct);

    protected override Task<List<AttributeDefinition>> LoadFilterableAsync(Guid? typeId, CancellationToken ct)
        => _db.AttributeDefinitions
            .Where(d => d.Filterable && (typeId == null || d.ProductTypeId == typeId))
            .Join(_db.ProductTypes.Where(t => t.Status == ProductTypeStatus.Active), d => d.ProductTypeId, t => t.Id, (d, _) => d)
            .ToListAsync(ct);
}
