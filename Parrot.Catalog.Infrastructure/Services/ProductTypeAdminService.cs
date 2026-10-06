using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Parrot.Catalog.Application.Audit;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Events;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

public sealed class ProductTypeAdminService : AdminServiceBase, IProductTypeAdminService
{
    private readonly IWriteSchemaRegistry _registry;

    public ProductTypeAdminService(CatalogDbContext db, ITenantContext tenant, TimeProvider clock, CatalogMetrics metrics, IWriteSchemaRegistry registry)
        : base(db, tenant, clock, metrics)
    {
        _registry = registry;
    }

    public Task<ProductTypeResponse> CreateAsync(CreateProductTypeRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        ProductType type = new ProductType
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Key = Guards.RequireMatch(request.Key, Guards.Key(), "key", "must match ^[a-z][a-z0-9_]{0,63}$."),
            Name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength),
            Kind = request.Kind,
            Version = 1,
            Status = ProductTypeStatus.Active,
        };
        Stamp(type, created: true);
        Db.ProductTypes.Add(type);

        List<AttributeDefinition> definitions = BuildDefinitions(type, 1, request.Attributes ?? []);
        Db.AttributeDefinitions.AddRange(definitions);

        Audit(AuditActions.Create, "product_type", type.Id, null, Map.Type(type, definitions));
        Enqueue(AggregateTypes.ProductType, type.Id, CatalogEventTypes.TypeVersionCreated, new { type_id = type.Id, key = type.Key, version = 1 });
        _registry.Invalidate(type.Id);
        await Task.CompletedTask;
        return Map.Type(type, definitions);
    }, ct);

    public Task<ProductTypeResponse> UpdateAsync(Guid id, UpdateProductTypeRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        ProductType type = await LoadTypeAsync(id, c);
        List<AttributeDefinition> definitions = await DefinitionsAsync(id, type.Version, c);
        ProductTypeResponse before = Map.Type(type, definitions);
        type.Name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength);
        type.Status = request.Status;
        Stamp(type, created: false);
        Audit(AuditActions.Update, "product_type", type.Id, before, Map.Type(type, definitions));
        _registry.Invalidate(type.Id);
        return Map.Type(type, definitions);
    }, ct);

    public async Task<ProductTypeResponse> GetAsync(Guid id, int? version, CancellationToken ct)
    {
        ProductType type = await LoadTypeAsync(id, ct);
        int v = version ?? type.Version;
        if (v < 1 || v > type.Version)
        {
            throw new NotFoundException($"Version {v} of product type '{type.Key}' was not found.");
        }

        return Map.Type(type, await DefinitionsAsync(id, v, ct));
    }

    public async Task<IReadOnlyList<ProductTypeResponse>> ListAsync(CancellationToken ct)
    {
        List<ProductType> types = await Db.ProductTypes.AsNoTracking().OrderBy(t => t.Key).ToListAsync(ct);
        List<AttributeDefinition> defs = await Db.AttributeDefinitions.AsNoTracking().ToListAsync(ct);
        return types.Select(t => Map.Type(t, defs.Where(d => d.ProductTypeId == t.Id && d.TypeVersion == t.Version))).ToArray();
    }

    public Task<AttributeDefinitionResponse> AddAttributeAsync(Guid typeId, AttributeDefinitionInput input, CancellationToken ct) => WriteAsync(async c =>
    {
        ProductType type = await LoadTypeAsync(typeId, c);
        if (input.Required && await Db.Products.AnyAsync(p => p.ProductTypeId == typeId && p.TypeVersion == type.Version && p.DeletedAt == null, c))
        {
            throw new ValidationException("A required attribute cannot be added to a version that already has products; create a new type version instead.");
        }

        AttributeDefinition definition = BuildDefinitions(type, type.Version, [input]).Single();
        Db.AttributeDefinitions.Add(definition);
        Audit(AuditActions.Create, "attribute_definition", definition.Id, null, Map.Definition(definition));
        _registry.Invalidate(typeId);
        return Map.Definition(definition);
    }, ct);

    public Task<AttributeDefinitionResponse> UpdateAttributeAsync(Guid typeId, Guid definitionId, UpdateAttributeDefinitionRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        ProductType type = await LoadTypeAsync(typeId, c);
        AttributeDefinition definition = await Db.AttributeDefinitions.FirstOrDefaultAsync(d => d.Id == definitionId && d.ProductTypeId == typeId, c)
            ?? throw new NotFoundException("Attribute definition", definitionId);
        if (definition.TypeVersion != type.Version)
        {
            throw new ConflictException("Only the current type version can be edited; older versions are immutable.");
        }

        AttributeDefinitionResponse before = Map.Definition(definition);
        CompiledAttribute old = CompiledAttribute.From(definition);

        if (request.Required && !definition.Required
            && await Db.Products.AnyAsync(p => p.ProductTypeId == typeId && p.TypeVersion == type.Version && p.DeletedAt == null, c))
        {
            throw new ValidationException("Making an attribute required would invalidate existing products; create a new type version instead.");
        }

        IReadOnlyList<string> newOptions = request.EnumOptions ?? [];
        if (definition.DataType is AttributeDataType.Enum or AttributeDataType.MultiEnum && old.EnumOptions.Any(o => !newOptions.Contains(o, StringComparer.Ordinal)))
        {
            throw new ValidationException("Enum options can be added inside a version but not removed; create a new type version to remove one.");
        }

        definition.Label = request.Label;
        definition.Unit = request.Unit;
        definition.Required = request.Required;
        definition.EnumOptions = definition.DataType is AttributeDataType.Enum or AttributeDataType.MultiEnum ? Json.ArrayText(newOptions) : null;
        definition.Filterable = request.Filterable;
        definition.Searchable = request.Searchable;
        definition.Embeddable = request.Embeddable;
        definition.DisplayGroup = request.DisplayGroup;
        definition.SortOrder = request.SortOrder;
        definition.Validation = request.Validation is null ? null : Json.ObjectText(request.Validation, "validation", 4096);
        Stamp(definition, created: false);
        AttributeDefinitionRules.Check(definition);

        Audit(AuditActions.Update, "attribute_definition", definition.Id, before, Map.Definition(definition));
        _registry.Invalidate(typeId);
        return Map.Definition(definition);
    }, ct);

    public Task<ProductTypeResponse> CreateVersionAsync(Guid typeId, CreateTypeVersionRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        ProductType type = await LoadTypeAsync(typeId, c);
        List<AttributeDefinition> current = await DefinitionsAsync(typeId, type.Version, c);
        int next = type.Version + 1;

        HashSet<string> remove = new HashSet<string>(request.Remove ?? [], StringComparer.Ordinal);
        Dictionary<string, AttributeDefinitionInput> change = (request.Change ?? []).ToDictionary(x => x.Key, StringComparer.Ordinal);
        foreach (string key in remove.Concat(change.Keys))
        {
            if (!current.Any(d => d.Key == key))
            {
                throw new ValidationException($"Attribute '{key}' does not exist on version {type.Version}.");
            }
        }

        List<AttributeDefinitionInput> inputs = new List<AttributeDefinitionInput>();
        foreach (AttributeDefinition d in current)
        {
            if (remove.Contains(d.Key))
            {
                continue;
            }

            inputs.Add(change.TryGetValue(d.Key, out AttributeDefinitionInput? changed) ? changed : ToInput(d));
        }

        foreach (AttributeDefinitionInput add in request.Add ?? [])
        {
            if (inputs.Any(i => i.Key == add.Key))
            {
                throw new ValidationException($"Attribute '{add.Key}' already exists; use change to modify it.");
            }

            inputs.Add(add);
        }

        List<AttributeDefinition> definitions = BuildDefinitions(type, next, inputs);
        Db.AttributeDefinitions.AddRange(definitions);
        type.Version = next;
        Stamp(type, created: false);

        Audit(AuditActions.VersionCreated, "product_type", type.Id, new { version = next - 1 }, Map.Type(type, definitions));
        Enqueue(AggregateTypes.ProductType, type.Id, CatalogEventTypes.TypeVersionCreated, new { type_id = type.Id, key = type.Key, version = next });
        _registry.Invalidate(typeId);
        return Map.Type(type, definitions);
    }, ct);

    public async Task<TypeSchemaResponse> GetSchemaAsync(Guid typeId, int? version, CancellationToken ct)
    {
        ProductType type = await LoadTypeAsync(typeId, ct);
        TypeSchema schema = await _registry.GetAsync(typeId, version ?? type.Version, ct);
        JsonElement product = JsonSerializer.SerializeToElement(schema.ToJsonSchema(AttributeScope.Product));
        JsonElement variant = JsonSerializer.SerializeToElement(schema.ToJsonSchema(AttributeScope.Variant));
        return new TypeSchemaResponse(type.Id, type.Key, schema.TypeVersion, product, variant);
    }

    private List<AttributeDefinition> BuildDefinitions(ProductType type, int version, IReadOnlyList<AttributeDefinitionInput> inputs)
    {
        if (inputs.Count > Limits.MaxBatchItems)
        {
            throw new ValidationException($"A type version may define at most {Limits.MaxBatchItems} attributes.");
        }

        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        List<AttributeDefinition> definitions = new List<AttributeDefinition>();
        foreach (AttributeDefinitionInput input in inputs)
        {
            if (!seen.Add(input.Key ?? string.Empty))
            {
                throw new ValidationException($"Attribute key '{input.Key}' is repeated.");
            }

            AttributeDefinition d = new AttributeDefinition
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                ProductTypeId = type.Id,
                TypeVersion = version,
                Key = input.Key ?? string.Empty,
                Label = input.Label,
                DataType = input.DataType,
                Unit = input.Unit,
                Required = input.Required,
                EnumOptions = input.EnumOptions is null ? null : Json.ArrayText(input.EnumOptions),
                Filterable = input.Filterable,
                Searchable = input.Searchable,
                Embeddable = input.Embeddable,
                VariantAxis = input.VariantAxis,
                DisplayGroup = input.DisplayGroup,
                SortOrder = input.SortOrder,
                Validation = input.Validation is null ? null : Json.ObjectText(input.Validation, $"attributes[{input.Key}].validation", 4096),
            };
            Stamp(d, created: true);
            AttributeDefinitionRules.Check(d);
            definitions.Add(d);
        }

        return definitions;
    }

    private static AttributeDefinitionInput ToInput(AttributeDefinition d)
    {
        CompiledAttribute c = CompiledAttribute.From(d);
        return new AttributeDefinitionInput(d.Key, d.Label, d.DataType, d.Unit, d.Required, c.EnumOptions.Count == 0 ? null : c.EnumOptions,
            d.Filterable, d.Searchable, d.Embeddable, d.VariantAxis, d.DisplayGroup, d.SortOrder, Json.ElementOrNull(d.Validation));
    }

    private async Task<ProductType> LoadTypeAsync(Guid id, CancellationToken ct)
        => await Db.ProductTypes.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Product type", id);

    private Task<List<AttributeDefinition>> DefinitionsAsync(Guid typeId, int version, CancellationToken ct)
        => Db.AttributeDefinitions.Where(d => d.ProductTypeId == typeId && d.TypeVersion == version).ToListAsync(ct);
}
