using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Catalog.Infrastructure.Services;

namespace Parrot.Catalog.Tools.Commands;

/// <summary>
/// Backfill for catalog.items: writes one ItemChanged (or ItemRemoved, for a retired
/// item) per product to the outbox, exactly as a save would, and the relay publishes
/// them. For items saved before whole-item events existed, or to let a consumer rebuild
/// its index. Safe to repeat: consumers treat a message as "the item is now this".
/// Deleted items are skipped; nothing downstream can have them unless it saw them go.
/// </summary>
public static class PublishItems
{
    public static async Task<int> RunAsync(IConfiguration configuration, Dictionary<string, string?> args)
    {
        List<Guid> tenants = Args.RequireGuids(args, "tenant");
        string? owner = args.TryGetValue("owner", out string? given) && !string.IsNullOrWhiteSpace(given) ? given.Trim() : null;
        int batchSize = Math.Clamp(Args.Int(args, "batch", 100), 1, 1000);
        bool dryRun = Args.Flag(args, "dry-run");

        await using ServiceProvider provider = Hosting.Services(configuration);
        int total = 0;
        foreach (Guid tenantId in tenants)
        {
            int count = await PublishAsync(() => Hosting.TenantScope(provider, tenantId, ownerId: owner), batchSize, dryRun, CancellationToken.None);
            Console.WriteLine(dryRun
                ? $"Tenant {tenantId}: {count} item(s) would be published."
                : $"Tenant {tenantId}: {count} item event(s) written to the outbox.");
            total += count;
        }

        if (tenants.Count > 1)
        {
            Console.WriteLine(dryRun ? $"{total} item(s) in all would be published." : $"{total} item event(s) in all.");
        }

        return 0;
    }

    /// <summary>
    /// Publishes every live item of the tenant that <paramref name="newScope"/> is set up
    /// for. Each call to it must return a fresh scope with that tenant established.
    /// </summary>
    public static async Task<int> PublishAsync(Func<IServiceScope> newScope, int batchSize, bool dryRun, CancellationToken ct)
    {
        List<Guid> ids;
        using (IServiceScope scope = newScope())
        {
            CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            ids = await db.Products.AsNoTracking().Where(p => p.DeletedAt == null).OrderBy(p => p.Id).Select(p => p.Id).ToListAsync(ct);
        }

        if (dryRun)
        {
            return ids.Count;
        }

        await RecordOwnerAsync(newScope, ct);

        // One transaction per batch, in a fresh scope, so a failure part way leaves the
        // earlier batches written and the change tracker stays small.
        foreach (Guid[] batch in ids.Chunk(batchSize))
        {
            using IServiceScope scope = newScope();
            CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            ItemEventWriter items = scope.ServiceProvider.GetRequiredService<ItemEventWriter>();

            await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
            foreach (Guid id in batch)
            {
                items.Touch(id);
            }

            await items.WriteAsync(ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return ids.Count;
    }

    /// <summary>
    /// Saves the owner the command was given against the workspace, as a write from a
    /// session would, so later events from tokens without the claim can still name it.
    /// </summary>
    private static async Task RecordOwnerAsync(Func<IServiceScope> newScope, CancellationToken ct)
    {
        using IServiceScope scope = newScope();
        ITenantContext tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        if (tenant.OwnerId is not { } owner)
        {
            return;
        }

        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Guid workspace = tenant.TenantId;
        Tenant? row = await db.Tenants.FirstOrDefaultAsync(t => t.Id == workspace, ct);
        if (row is not null && row.OwnerId != owner)
        {
            row.OwnerId = owner;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }
}
