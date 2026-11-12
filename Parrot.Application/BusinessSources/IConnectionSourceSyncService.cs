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

    /// <summary>
    /// Marks all active sources for the connection inactive and publishes an updated workspace snapshot.
    /// Call best-effort after a channel connection is disabled.
    /// </summary>
    Task DeactivateConnectionSourcesAsync(string workspaceId, Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Resets the metadata sync status of the Profile source owned by the connection back to Pending
    /// so the metadata worker will re-fetch it. Throws NotFoundException when no active Profile source exists.
    /// </summary>
    Task RequestMetadataRefreshAsync(string workspaceId, Guid connectionId, CancellationToken ct = default);
}
