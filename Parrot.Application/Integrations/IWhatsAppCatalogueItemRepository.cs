using Parrot.Domain.Entities;

namespace Parrot.Application.Integrations;

public interface IWhatsAppCatalogueItemRepository
{
    /// <summary>Returns all non-deleted items for a connection. Used by the sync service to diff against Meta.</summary>
    Task<IReadOnlyList<WhatsAppCatalogueItem>> GetActiveByConnectionAsync(Guid connectionId, CancellationToken ct);

    /// <summary>Persists a batch of new items in a single round-trip.</summary>
    Task AddRangeAsync(IEnumerable<WhatsAppCatalogueItem> items, CancellationToken ct);

    /// <summary>Saves updated fields on a batch of existing items (including restoring tombstoned ones).</summary>
    Task UpdateRangeAsync(IEnumerable<WhatsAppCatalogueItem> items, CancellationToken ct);

    /// <summary>
    /// Soft-deletes all non-deleted items for a connection whose <see cref="WhatsAppCatalogueItem.RetailerId"/>
    /// is not in <paramref name="presentRetailerIds"/>. Returns the number of items tombstoned.
    /// </summary>
    Task<int> TombstoneMissingAsync(Guid connectionId, IReadOnlyCollection<string> presentRetailerIds, DateTime tombstonedAt, CancellationToken ct);

    /// <summary>Returns a page of non-deleted items for a connection, ordered by name.</summary>
    Task<(IReadOnlyList<WhatsAppCatalogueItem> Items, int Total)> GetPageByConnectionAsync(
        Guid connectionId, int page, int pageSize, CancellationToken ct);
}
