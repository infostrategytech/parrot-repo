namespace Parrot.Application.BusinessSources;

public interface IConnectionSourceSyncService
{
    // Called once a WhatsApp connection has a confirmed PhoneNumberId.
    Task SyncWhatsAppProfileAsync(string workspaceId, Guid connectionId, string phoneNumberId, CancellationToken ct = default);

    // Called each time a WhatsApp catalogue sync runs.
    // Upserts the catalog source when catalogId is non-null; deactivates it when null.
    Task SyncWhatsAppCatalogAsync(string workspaceId, Guid connectionId, string? catalogId, CancellationToken ct = default);

    // Called when an Instagram account is confirmed via the access token.
    Task SyncInstagramProfileAsync(string workspaceId, Guid connectionId, string accountId, string username, CancellationToken ct = default);

    // Called when a Facebook Page is saved.
    Task SyncFacebookPageAsync(string workspaceId, Guid connectionId, string pageId, string pageName, CancellationToken ct = default);
}
