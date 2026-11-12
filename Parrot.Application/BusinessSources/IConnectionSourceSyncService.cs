namespace Parrot.Application.BusinessSources;

public interface IConnectionSourceSyncService
{
    // Called once a WhatsApp connection has a confirmed PhoneNumberId.
    // Upserts the WhatsApp profile source and publishes the workspace snapshot.
    Task SyncWhatsAppProfileAsync(string workspaceId, Guid connectionId, string phoneNumberId, CancellationToken ct = default);

    // Called each time a WhatsApp catalogue sync runs.
    // Upserts the catalog source when catalogId is non-null; deactivates it when null.
    // Always publishes the workspace snapshot so the AI sees the current state.
    Task SyncWhatsAppCatalogAsync(string workspaceId, Guid connectionId, string? catalogId, CancellationToken ct = default);
}
