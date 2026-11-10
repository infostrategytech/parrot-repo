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

public sealed class CatalogAdminService : AdminServiceBase, ICatalogAdminService
{
    public CatalogAdminService(CatalogDbContext db, ITenantContext tenant, TimeProvider clock, CatalogMetrics metrics, ItemEventWriter itemEvents)
        : base(db, tenant, clock, metrics, itemEvents)
    {
    }

    public Task<CatalogResponse> CreateAsync(CreateCatalogRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        CatalogEntity entity = new CatalogEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Key = Guards.RequireMatch(request.Key, Guards.Key(), "key", "must match ^[a-z][a-z0-9_]{0,63}$."),
            Name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength),
            DefaultCurrency = RequireCurrency(request.DefaultCurrency),
        };
        Stamp(entity, created: true);
        Db.Catalogs.Add(entity);
        Audit(AuditActions.Create, "catalog", entity.Id, null, Map.Catalog(entity));
        await Task.CompletedTask;
        return Map.Catalog(entity);
    }, ct);

    public Task<CatalogResponse> UpdateAsync(Guid id, UpdateCatalogRequest request, CancellationToken ct) => WriteAsync(async c =>
    {
        CatalogEntity entity = await LoadAsync(id, c);
        CatalogResponse before = Map.Catalog(entity);
        entity.Name = Guards.RequireText(request.Name, "name", Limits.MaxNameLength);
        entity.DefaultCurrency = RequireCurrency(request.DefaultCurrency);
        entity.Status = request.Status;
        Stamp(entity, created: false);
        Audit(AuditActions.Update, "catalog", entity.Id, before, Map.Catalog(entity));
        return Map.Catalog(entity);
    }, ct);

    public async Task<CatalogResponse> GetAsync(Guid id, CancellationToken ct) => Map.Catalog(await LoadAsync(id, ct));

    public async Task<IReadOnlyList<CatalogResponse>> ListAsync(CancellationToken ct)
        => (await Db.Catalogs.AsNoTracking().Where(c => c.DeletedAt == null).OrderBy(c => c.Key).ToListAsync(ct)).Select(Map.Catalog).ToArray();

    public Task DeleteAsync(Guid id, CancellationToken ct) => WriteAsync(async c =>
    {
        CatalogEntity entity = await LoadAsync(id, c);
        bool hasProducts = await Db.Products.AnyAsync(p => p.CatalogId == id && p.DeletedAt == null, c);
        if (hasProducts)
        {
            throw new ConflictException("The catalog still has products; delete or move them first.");
        }

        entity.DeletedAt = Now;
        Stamp(entity, created: false);
        Audit(AuditActions.Delete, "catalog", entity.Id, Map.Catalog(entity), null);
    }, ct);

    private async Task<CatalogEntity> LoadAsync(Guid id, CancellationToken ct)
        => await Db.Catalogs.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct) ?? throw new NotFoundException("Catalog", id);

    private static string RequireCurrency(string? code)
        => Iso4217.IsValid(code) ? code! : throw new ValidationException("default_currency must be an ISO 4217 code such as NGN or USD.");
}
