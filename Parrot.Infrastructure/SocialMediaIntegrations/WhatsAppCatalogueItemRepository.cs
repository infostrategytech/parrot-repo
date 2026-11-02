using Microsoft.EntityFrameworkCore;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

public class WhatsAppCatalogueItemRepository(ApplicationDbContext db) : IWhatsAppCatalogueItemRepository
{
    public async Task<IReadOnlyList<WhatsAppCatalogueItem>> GetActiveByConnectionAsync(Guid connectionId, CancellationToken ct) =>
        await db.WhatsAppCatalogueItems.AsNoTracking()
            .Where(i => i.ConnectionId == connectionId && i.DeletedAt == null)
            .ToListAsync(ct);

    public async Task AddRangeAsync(IEnumerable<WhatsAppCatalogueItem> items, CancellationToken ct)
    {
        db.WhatsAppCatalogueItems.AddRange(items);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateRangeAsync(IEnumerable<WhatsAppCatalogueItem> items, CancellationToken ct)
    {
        db.WhatsAppCatalogueItems.UpdateRange(items);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> TombstoneMissingAsync(
        Guid connectionId, IReadOnlyCollection<string> presentRetailerIds, DateTime tombstonedAt, CancellationToken ct)
    {
        // When presentRetailerIds is empty the catalogue had no approved products, so every
        // existing item for this connection is absent — tombstone them all.
        // PostgreSQL does not allow IN () with an empty list, so we omit the NOT IN clause.
        IQueryable<WhatsAppCatalogueItem> query = db.WhatsAppCatalogueItems
            .Where(i => i.ConnectionId == connectionId && i.DeletedAt == null);

        if (presentRetailerIds.Count > 0)
            query = query.Where(i => !presentRetailerIds.Contains(i.RetailerId));

        return await query.ExecuteUpdateAsync(s => s
            .SetProperty(i => i.DeletedAt, tombstonedAt)
            .SetProperty(i => i.UpdatedAt, tombstonedAt), ct);
    }

    public async Task<(IReadOnlyList<WhatsAppCatalogueItem> Items, int Total)> GetPageByConnectionAsync(
        Guid connectionId, int page, int pageSize, CancellationToken ct)
    {
        IQueryable<WhatsAppCatalogueItem> query = db.WhatsAppCatalogueItems.AsNoTracking()
            .Where(i => i.ConnectionId == connectionId && i.DeletedAt == null)
            .OrderBy(i => i.Name);

        int total = await query.CountAsync(ct);
        List<WhatsAppCatalogueItem> items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }
}
