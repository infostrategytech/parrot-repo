using Parrot.Application.Contracts.WhatsAppCatalogue;

namespace Parrot.Application.Integrations;

public interface IWhatsAppCatalogueSyncService
{
    Task<CatalogueSyncResult> SyncAsync(string workspaceId, Guid connectionId, CancellationToken ct);
    Task<CatalogueItemsResponse> GetProductsAsync(string workspaceId, Guid connectionId, int page, int pageSize, CancellationToken ct);
}
