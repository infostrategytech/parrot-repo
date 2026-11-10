using Microsoft.EntityFrameworkCore;
using Parrot.Catalog.Application.Audit;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Events;
using Parrot.Catalog.Application.Pagination;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

public sealed class ProductAdminService : AdminServiceBase, IProductAdminService
{
    private const string ListSort = "created_at";

    private readonly IWriteSchemaRegistry _registry;
    private readonly DerivedDataRebuilder _rebuilder;

    public ProductAdminService(CatalogDbContext db, ITenantContext tenant, TimeProvider clock, CatalogMetrics metrics, ItemEventWriter itemEvents, IWriteSchemaRegistry registry, DerivedDataRebuilder rebuilder)
        : base(db, tenant, clock, metrics, itemEvents)
    {
        _registry = registry;
        _rebuilder = rebuilder;
    }

    public Task<ProductAdminResponse> CreateAsync(CreateProductRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        Guid catalogId = Guards.RequireId(request.CatalogId, "catalog_id");
        if (!await Db.Catalogs.AnyAsync(x => x.Id == catalogId && x.DeletedAt == null, c))
        {
            throw new NotFoundException("Catalog", catalogId);
        }

        ProductType type = await Db.ProductTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.ProductTypeId, c)
            ?? throw new NotFoundException("Product type", request.ProductTypeId);
        if (type.Status != ProductTypeStatus.Active)
        {
            throw new ValidationException($"Product type '{type.Key}' is deprecated; new products cannot use it.");
        }

        await RequireCategoryAsync(request.CategoryId, catalogId, c);
        TypeSchema schema = await _registry.GetAsync(type.Id, type.Version, c);
        string attributes = ValidateAttributes(schema, request.Attributes, AttributeScope.Product);

        Product product = new Product
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            CatalogId = catalogId,
            ProductTypeId = type.Id,
            TypeVersion = type.Version,
            CategoryId = request.CategoryId,
            Sku = Guards.RequireMatch(request.Sku, Guards.Sku(), "sku", "must be 1-64 letters, digits, dots, underscores or hyphens."),
            Name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength),
            Slug = request.Slug is null ? Guards.Slugify(request.Name) : Guards.RequireMatch(request.Slug, Guards.Slug(), "slug", "must be lowercase words separated by single hyphens."),
            Description = Guards.OptionalText(request.Description, "description", Limits.MaxDescriptionLength),
            Status = ProductStatus.Draft,
            Attributes = attributes,
            CreatedBy = Tenant.Actor,
            UpdatedBy = Tenant.Actor,
        };
        Stamp(product, created: true);
        Db.Products.Add(product);
        await SaveAsync(c);
        await _rebuilder.RebuildAsync(product, schema, c);

        ProductAdminResponse response = Map.ProductAdmin(product, type, []);
        Audit(AuditActions.Create, "product", product.Id, null, response);
        Enqueue(AggregateTypes.Product, product.Id, CatalogEventTypes.ProductCreated, EventPayload(product));
        return response;
    }, ct);

    public Task<ProductAdminResponse> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        Product product = await LoadProductAsync(id, c);
        if (product.Status == ProductStatus.Retired)
        {
            throw new ConflictException("Retired products are frozen.");
        }

        ProductType type = await LoadTypeAsync(product.ProductTypeId, c);
        List<ProductVariant> variants = await VariantsAsync(id, c);
        ProductAdminResponse before = Map.ProductAdmin(product, type, variants);

        if (request.Status is { } status)
        {
            if (status is not (ProductStatus.Draft or ProductStatus.Review))
            {
                throw new ValidationException("status may only be set to draft or review here; use the publish and retire endpoints for the rest.");
            }

            if (product.Status == ProductStatus.Published)
            {
                throw new ConflictException("A published product cannot move back to draft or review; retire it instead.");
            }

            product.Status = status;
        }

        await RequireCategoryAsync(request.CategoryId, product.CatalogId, c);
        TypeSchema schema = await _registry.GetAsync(product.ProductTypeId, product.TypeVersion, c);
        product.Attributes = ValidateAttributes(schema, request.Attributes, AttributeScope.Product);
        product.CategoryId = request.CategoryId;
        product.Name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength);
        product.Slug = request.Slug is null ? product.Slug : Guards.RequireMatch(request.Slug, Guards.Slug(), "slug", "must be lowercase words separated by single hyphens.");
        product.Description = Guards.OptionalText(request.Description, "description", Limits.MaxDescriptionLength);
        product.UpdatedBy = Tenant.Actor;
        Stamp(product, created: false);

        await _rebuilder.RebuildAsync(product, schema, c);
        if (product.Status == ProductStatus.Published)
        {
            await _rebuilder.SnapshotAsync(product, schema, Tenant.Actor, c);
        }

        ProductAdminResponse response = Map.ProductAdmin(product, type, variants);
        Audit(AuditActions.Update, "product", product.Id, before, response);
        Enqueue(AggregateTypes.Product, product.Id, CatalogEventTypes.ProductUpdated, EventPayload(product));
        return response;
    }, ct);

    public async Task<ProductAdminResponse> GetAsync(Guid id, CancellationToken ct)
    {
        Product product = await LoadProductAsync(id, ct);
        return Map.ProductAdmin(product, await LoadTypeAsync(product.ProductTypeId, ct), await VariantsAsync(id, ct));
    }

    public async Task<PagedResponse<ProductAdminResponse>> ListAsync(ProductListQuery query, CancellationToken ct)
    {
        int limit = PageSize.Clamp(query.Limit, Limits.MaxPageSize, Limits.DefaultPageSize);
        Cursor? cursor = Cursor.Decode(query.Cursor, ListSort);

        IQueryable<Product> q = Db.Products.AsNoTracking().Where(p => p.DeletedAt == null);
        if (query.CatalogId is { } catalogId)
        {
            q = q.Where(p => p.CatalogId == catalogId);
        }

        if (query.Status is { } status)
        {
            q = q.Where(p => p.Status == status);
        }

        if (query.ProductTypeId is { } typeId)
        {
            q = q.Where(p => p.ProductTypeId == typeId);
        }

        if (query.CategoryId is { } categoryId)
        {
            q = q.Where(p => p.CategoryId == categoryId);
        }

        if (cursor is not null && long.TryParse(cursor.Value, out long ticks))
        {
            DateTime after = new DateTime(ticks, DateTimeKind.Utc);
            q = q.Where(p => p.CreatedAt < after || (p.CreatedAt == after && p.Id.CompareTo(cursor.Id) < 0));
        }

        List<Product> rows = await q.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Take(limit + 1).ToListAsync(ct);
        bool more = rows.Count > limit;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        List<Guid> ids = rows.Select(p => p.Id).ToList();
        List<ProductType> types = await Db.ProductTypes.AsNoTracking().ToListAsync(ct);
        List<ProductVariant> variants = await Db.ProductVariants.AsNoTracking().Where(v => ids.Contains(v.ProductId) && v.DeletedAt == null).ToListAsync(ct);

        List<ProductAdminResponse> items = rows
            .Select(p => Map.ProductAdmin(p, types.First(t => t.Id == p.ProductTypeId), variants.Where(v => v.ProductId == p.Id).ToList()))
            .ToList();
        string? next = more ? new Cursor(ListSort, rows[^1].CreatedAt.Ticks.ToString(), rows[^1].Id).Encode() : null;
        return new PagedResponse<ProductAdminResponse>(items, next);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct) => WriteAsync(async c =>
    {
        Product product = await LoadProductAsync(id, c);
        ProductType type = await LoadTypeAsync(product.ProductTypeId, c);
        product.DeletedAt = Now;
        product.UpdatedBy = Tenant.Actor;
        Stamp(product, created: false);
        Audit(AuditActions.Delete, "product", product.Id, Map.ProductAdmin(product, type, []), null);
        Enqueue(AggregateTypes.Product, product.Id, CatalogEventTypes.ProductRetired, EventPayload(product, reason: "deleted"));
    }, ct);

    public Task<ProductAdminResponse> PublishAsync(Guid id, CancellationToken ct) => WriteAsync(async c =>
    {
        Product product = await LoadProductAsync(id, c);
        if (product.Status == ProductStatus.Published)
        {
            throw new ConflictException("The product is already published.");
        }

        if (product.Status == ProductStatus.Retired)
        {
            throw new ConflictException("Retired products cannot be published again; create a new product.");
        }

        ProductType type = await LoadTypeAsync(product.ProductTypeId, c);
        TypeSchema schema = await _registry.GetAsync(product.ProductTypeId, product.TypeVersion, c);
        IReadOnlyList<string> errors = schema.Validate(product.Attributes, AttributeScope.Product);
        List<ProductVariant> variants = await VariantsAsync(id, c);
        foreach (ProductVariant v in variants.Where(v => v.Status == VariantStatus.Active))
        {
            errors = errors.Concat(schema.Validate(v.Attributes, AttributeScope.Variant).Select(e => $"variant {v.Sku}: {e}")).ToList();
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        ProductAdminResponse before = Map.ProductAdmin(product, type, variants);
        product.Status = ProductStatus.Published;
        product.PublishedAt = Now;
        product.UpdatedBy = Tenant.Actor;
        Stamp(product, created: false);

        // One transaction: snapshot, derived index, canonical document, outbox row.
        await _rebuilder.SnapshotAsync(product, schema, Tenant.Actor, c);
        await _rebuilder.RebuildAsync(product, schema, c);
        await SaveAsync(c);
        await _rebuilder.RerenderSourcesOfAsync(product.Id, c);

        ProductAdminResponse response = Map.ProductAdmin(product, type, variants);
        Audit(AuditActions.Publish, "product", product.Id, before, response);
        Enqueue(AggregateTypes.Product, product.Id, CatalogEventTypes.ProductPublished, EventPayload(product));
        return response;
    }, ct);

    public Task<ProductAdminResponse> RetireAsync(Guid id, CancellationToken ct) => WriteAsync(async c =>
    {
        Product product = await LoadProductAsync(id, c);
        if (product.Status != ProductStatus.Published)
        {
            throw new ConflictException("Only published products can be retired.");
        }

        ProductType type = await LoadTypeAsync(product.ProductTypeId, c);
        List<ProductVariant> variants = await VariantsAsync(id, c);
        ProductAdminResponse before = Map.ProductAdmin(product, type, variants);
        product.Status = ProductStatus.Retired;
        product.RetiredAt = Now;
        product.UpdatedBy = Tenant.Actor;
        Stamp(product, created: false);
        await SaveAsync(c);
        await _rebuilder.RerenderSourcesOfAsync(product.Id, c);

        ProductAdminResponse response = Map.ProductAdmin(product, type, variants);
        Audit(AuditActions.Retire, "product", product.Id, before, response);
        Enqueue(AggregateTypes.Product, product.Id, CatalogEventTypes.ProductRetired, EventPayload(product, reason: "retired"));
        return response;
    }, ct);

    public Task<ProductAdminResponse> ReactivateAsync(Guid id, CancellationToken ct) => WriteAsync(async c =>
    {
        Product product = await LoadProductAsync(id, c);
        if (product.Status != ProductStatus.Retired)
        {
            throw new ConflictException("Only retired products can be reactivated.");
        }

        ProductType type = await LoadTypeAsync(product.ProductTypeId, c);
        List<ProductVariant> variants = await VariantsAsync(id, c);
        ProductAdminResponse before = Map.ProductAdmin(product, type, variants);
        product.Status = ProductStatus.Draft;
        product.RetiredAt = null;
        product.UpdatedBy = Tenant.Actor;
        Stamp(product, created: false);
        TypeSchema schema = await _registry.GetAsync(product.ProductTypeId, product.TypeVersion, c);
        await _rebuilder.RebuildAsync(product, schema, c);

        ProductAdminResponse response = Map.ProductAdmin(product, type, variants);
        Audit(AuditActions.Update, "product", product.Id, before, response);
        Enqueue(AggregateTypes.Product, product.Id, CatalogEventTypes.ProductUpdated, EventPayload(product));
        return response;
    }, ct);

    public Task<VariantResponse> CreateVariantAsync(Guid productId, CreateVariantRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        Product product = await LoadProductAsync(productId, c);
        if (product.Status == ProductStatus.Retired)
        {
            throw new ConflictException("Retired products are frozen.");
        }

        TypeSchema schema = await _registry.GetAsync(product.ProductTypeId, product.TypeVersion, c);
        ProductVariant variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            CatalogId = product.CatalogId,
            ProductId = product.Id,
            Sku = Guards.RequireMatch(request.Sku, Guards.Sku(), "sku", "must be 1-64 letters, digits, dots, underscores or hyphens."),
            Name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength),
            Attributes = ValidateAttributes(schema, request.Attributes, AttributeScope.Variant),
            Status = request.Status,
            SortOrder = request.SortOrder,
        };
        Stamp(variant, created: true);
        Db.ProductVariants.Add(variant);
        await SaveAsync(c);
        await TouchProductAsync(product, schema, c);

        VariantResponse response = Map.Variant(variant);
        Audit(AuditActions.Create, "product_variant", variant.Id, null, response);
        return response;
    }, ct);

    public Task<VariantResponse> UpdateVariantAsync(Guid productId, Guid variantId, UpdateVariantRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        Product product = await LoadProductAsync(productId, c);
        if (product.Status == ProductStatus.Retired)
        {
            throw new ConflictException("Retired products are frozen.");
        }

        ProductVariant variant = await LoadVariantAsync(productId, variantId, c);
        VariantResponse before = Map.Variant(variant);
        TypeSchema schema = await _registry.GetAsync(product.ProductTypeId, product.TypeVersion, c);
        variant.Name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength);
        variant.Attributes = ValidateAttributes(schema, request.Attributes, AttributeScope.Variant);
        variant.Status = request.Status;
        variant.SortOrder = request.SortOrder;
        Stamp(variant, created: false);
        await SaveAsync(c);
        await TouchProductAsync(product, schema, c);

        VariantResponse response = Map.Variant(variant);
        Audit(AuditActions.Update, "product_variant", variant.Id, before, response);
        return response;
    }, ct);

    public Task DeleteVariantAsync(Guid productId, Guid variantId, CancellationToken ct) => WriteAsync(async c =>
    {
        Product product = await LoadProductAsync(productId, c);
        ProductVariant variant = await LoadVariantAsync(productId, variantId, c);
        TypeSchema schema = await _registry.GetAsync(product.ProductTypeId, product.TypeVersion, c);
        variant.DeletedAt = Now;
        Stamp(variant, created: false);
        await SaveAsync(c);
        await TouchProductAsync(product, schema, c);
        Audit(AuditActions.Delete, "product_variant", variant.Id, Map.Variant(variant), null);
    }, ct);

    /// <summary>Variant changes change the product's truth: rebuild derived data, version it if published, and announce it.</summary>
    private async Task TouchProductAsync(Product product, TypeSchema schema, CancellationToken ct)
    {
        product.UpdatedBy = Tenant.Actor;
        Stamp(product, created: false);
        await _rebuilder.RebuildAsync(product, schema, ct);
        if (product.Status == ProductStatus.Published)
        {
            await _rebuilder.SnapshotAsync(product, schema, Tenant.Actor, ct);
        }

        Enqueue(AggregateTypes.Product, product.Id, CatalogEventTypes.ProductUpdated, EventPayload(product));
    }

    private static string ValidateAttributes(TypeSchema schema, System.Text.Json.JsonElement attributes, AttributeScope scope)
    {
        string text = Json.ObjectText(attributes, "attributes");
        IReadOnlyList<string> errors = schema.Validate(text, scope);
        return errors.Count == 0 ? text : throw new ValidationException(errors);
    }

    private async Task RequireCategoryAsync(Guid? categoryId, Guid catalogId, CancellationToken ct)
    {
        if (categoryId is { } id && !await Db.Categories.AnyAsync(x => x.Id == id && x.CatalogId == catalogId && x.DeletedAt == null, ct))
        {
            throw new ValidationException("category_id must be a category of the same catalog.");
        }
    }

    private static object EventPayload(Product p, string? reason = null) => new
    {
        product_id = p.Id,
        catalog_id = p.CatalogId,
        sku = p.Sku,
        status = EnumNames.ToSnake(p.Status),
        version = p.Version,
        type_version = p.TypeVersion,
        product_type_id = p.ProductTypeId,
        reason,
    };

    private async Task<Product> LoadProductAsync(Guid id, CancellationToken ct)
        => await Db.Products.FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct) ?? throw new NotFoundException("Product", id);

    private async Task<ProductType> LoadTypeAsync(Guid id, CancellationToken ct)
        => await Db.ProductTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Product type", id);

    private async Task<ProductVariant> LoadVariantAsync(Guid productId, Guid variantId, CancellationToken ct)
        => await Db.ProductVariants.FirstOrDefaultAsync(v => v.Id == variantId && v.ProductId == productId && v.DeletedAt == null, ct)
            ?? throw new NotFoundException("Product variant", variantId);

    private Task<List<ProductVariant>> VariantsAsync(Guid productId, CancellationToken ct)
        => Db.ProductVariants.AsNoTracking().Where(v => v.ProductId == productId && v.DeletedAt == null).ToListAsync(ct);
}
