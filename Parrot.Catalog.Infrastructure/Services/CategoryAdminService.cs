using Microsoft.EntityFrameworkCore;
using Parrot.Catalog.Application.Audit;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Application.Validation;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>Categories plus their closure table, so "every product under Electronics" is one join.</summary>
public sealed class CategoryAdminService : AdminServiceBase, ICategoryAdminService
{
    private readonly DerivedDataRebuilder _rebuilder;

    public CategoryAdminService(CatalogDbContext db, ITenantContext tenant, TimeProvider clock, CatalogMetrics metrics, ItemEventWriter itemEvents, DerivedDataRebuilder rebuilder)
        : base(db, tenant, clock, metrics, itemEvents)
    {
        _rebuilder = rebuilder;
    }

    public Task<CategoryResponse> CreateAsync(CreateCategoryRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        Guid catalogId = Guards.RequireId(request.CatalogId, "catalog_id");
        if (!await Db.Catalogs.AnyAsync(x => x.Id == catalogId && x.DeletedAt == null, c))
        {
            throw new NotFoundException("Catalog", catalogId);
        }

        Category? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = await LoadAsync(parentId, c);
            if (parent.CatalogId != catalogId)
            {
                throw new ValidationException("parent_id must belong to the same catalog.");
            }
        }

        Category category = new Category
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            CatalogId = catalogId,
            ParentId = parent?.Id,
            Key = Guards.RequireMatch(request.Key, Guards.Key(), "key", "must match ^[a-z][a-z0-9_]{0,63}$."),
            Name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength),
            Slug = request.Slug is null ? Guards.Slugify(request.Name) : Guards.RequireMatch(request.Slug, Guards.Slug(), "slug", "must be lowercase words separated by single hyphens."),
            Description = Guards.OptionalText(request.Description, "description", 2000),
            SortOrder = request.SortOrder,
        };
        Stamp(category, created: true);
        Db.Categories.Add(category);

        List<CategoryClosure> ancestors = parent is null
            ? []
            : await Db.CategoryClosure.AsNoTracking().Where(x => x.DescendantId == parent.Id).ToListAsync(c);
        if (ancestors.Count >= Limits.MaxCategoryDepth)
        {
            throw new ValidationException($"Categories may be nested at most {Limits.MaxCategoryDepth} levels deep.");
        }

        Db.CategoryClosure.Add(new CategoryClosure { TenantId = TenantId, CatalogId = catalogId, AncestorId = category.Id, DescendantId = category.Id, Depth = 0 });
        foreach (CategoryClosure a in ancestors)
        {
            Db.CategoryClosure.Add(new CategoryClosure { TenantId = TenantId, CatalogId = catalogId, AncestorId = a.AncestorId, DescendantId = category.Id, Depth = a.Depth + 1 });
        }

        await SaveAsync(c);
        CategoryResponse response = Map.Category(category, await _rebuilder.CategoryPathAsync(category.Id, c));
        Audit(AuditActions.Create, "category", category.Id, null, response);
        return response;
    }, ct);

    public Task<CategoryResponse> UpdateAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        Category category = await LoadAsync(id, c);
        CategoryResponse before = Map.Category(category, await _rebuilder.CategoryPathAsync(category.Id, c));

        category.Name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength);
        category.Slug = request.Slug is null ? category.Slug : Guards.RequireMatch(request.Slug, Guards.Slug(), "slug", "must be lowercase words separated by single hyphens.");
        category.Description = Guards.OptionalText(request.Description, "description", 2000);
        category.SortOrder = request.SortOrder;

        if (request.ParentId != category.ParentId)
        {
            await ReparentAsync(category, request.ParentId, c);
        }

        Stamp(category, created: false);
        await SaveAsync(c);
        CategoryResponse response = Map.Category(category, await _rebuilder.CategoryPathAsync(category.Id, c));
        Audit(AuditActions.Update, "category", category.Id, before, response);

        // An item's text carries its category path, so a rename or move changes every
        // item at or below this category; each gets a fresh whole-item event.
        if (!before.Path.SequenceEqual(response.Path, StringComparer.Ordinal))
        {
            List<Guid> affected = await Db.CategoryClosure.AsNoTracking()
                .Where(x => x.AncestorId == category.Id)
                .Join(Db.Products.AsNoTracking().Where(p => p.DeletedAt == null), x => x.DescendantId, p => p.CategoryId, (x, p) => p.Id)
                .ToListAsync(c);
            foreach (Guid productId in affected)
            {
                ItemEvents.Touch(productId);
            }
        }

        return response;
    }, ct);

    public async Task<CategoryResponse> GetAsync(Guid id, CancellationToken ct)
    {
        Category category = await LoadAsync(id, ct);
        return Map.Category(category, await _rebuilder.CategoryPathAsync(category.Id, ct));
    }

    public async Task<IReadOnlyList<CategoryResponse>> ListAsync(Guid? catalogId, CancellationToken ct)
    {
        List<Category> categories = await Db.Categories.AsNoTracking()
            .Where(x => x.DeletedAt == null && (catalogId == null || x.CatalogId == catalogId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .ToListAsync(ct);
        List<CategoryResponse> result = new List<CategoryResponse>(categories.Count);
        foreach (Category category in categories)
        {
            result.Add(Map.Category(category, await _rebuilder.CategoryPathAsync(category.Id, ct)));
        }

        return result;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct) => WriteAsync(async c =>
    {
        Category category = await LoadAsync(id, c);
        if (await Db.Categories.AnyAsync(x => x.ParentId == id && x.DeletedAt == null, c))
        {
            throw new ConflictException("The category still has child categories.");
        }

        if (await Db.Products.AnyAsync(p => p.CategoryId == id && p.DeletedAt == null, c))
        {
            throw new ConflictException("The category still has products; move them first.");
        }

        category.DeletedAt = Now;
        Stamp(category, created: false);
        await Db.CategoryClosure.Where(x => x.DescendantId == id || x.AncestorId == id).ExecuteDeleteAsync(c);
        Audit(AuditActions.Delete, "category", category.Id, Map.Category(category, []), null);
    }, ct);

    private async Task ReparentAsync(Category category, Guid? newParentId, CancellationToken ct)
    {
        List<CategoryClosure> subtree = await Db.CategoryClosure.AsNoTracking().Where(x => x.AncestorId == category.Id).ToListAsync(ct);
        HashSet<Guid> subtreeIds = subtree.Select(x => x.DescendantId).ToHashSet();
        int subtreeHeight = subtree.Max(x => x.Depth);

        List<CategoryClosure> newAncestors = [];
        if (newParentId is { } parentId)
        {
            Category parent = await LoadAsync(parentId, ct);
            if (parent.CatalogId != category.CatalogId)
            {
                throw new ValidationException("parent_id must belong to the same catalog.");
            }

            if (subtreeIds.Contains(parentId))
            {
                throw new ValidationException("A category cannot be moved under itself or one of its descendants.");
            }

            newAncestors = await Db.CategoryClosure.AsNoTracking().Where(x => x.DescendantId == parentId).ToListAsync(ct);
            if (newAncestors.Count + 1 + subtreeHeight > Limits.MaxCategoryDepth)
            {
                throw new ValidationException($"Categories may be nested at most {Limits.MaxCategoryDepth} levels deep.");
            }
        }

        // Cut the subtree from its old ancestors, then attach it to the new ones.
        await Db.CategoryClosure
            .Where(x => subtreeIds.Contains(x.DescendantId) && !subtreeIds.Contains(x.AncestorId))
            .ExecuteDeleteAsync(ct);
        foreach (CategoryClosure a in newAncestors)
        {
            foreach (CategoryClosure d in subtree)
            {
                Db.CategoryClosure.Add(new CategoryClosure { TenantId = TenantId, CatalogId = category.CatalogId, AncestorId = a.AncestorId, DescendantId = d.DescendantId, Depth = a.Depth + 1 + d.Depth });
            }
        }

        category.ParentId = newParentId;
    }

    private async Task<Category> LoadAsync(Guid id, CancellationToken ct)
        => await Db.Categories.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct) ?? throw new NotFoundException("Category", id);
}
