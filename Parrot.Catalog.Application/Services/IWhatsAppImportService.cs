using Parrot.Catalog.Application.Contracts;

namespace Parrot.Catalog.Application.Services;

public interface IWhatsAppImportService
{
    Task<WhatsAppSyncResponse> SyncAsync(WhatsAppSyncRequest request, CancellationToken ct);

    Task<WhatsAppRetireMissingResponse> RetireMissingAsync(WhatsAppRetireMissingRequest request, CancellationToken ct);

    Task<PagedResponse<WhatsAppProductResponse>> GetProductsAsync(WhatsAppProductsQuery query, CancellationToken ct);
}
