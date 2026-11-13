using Parrot.Web.Features.Dashboard.Channels.Models;

namespace Parrot.Web.Features.Dashboard.Channels.Services;

public interface IIntegrationsService
{
    Task SaveWhatsAppAccessTokenAsync(Guid id, string accessToken, string token, CancellationToken cancellationToken = default);
    Task SaveWhatsAppAppSecretAsync(Guid id, string appSecret, string token, CancellationToken cancellationToken = default);
    Task SaveFacebookAppSecretAsync(string appSecret, string token, CancellationToken cancellationToken = default);
    Task<FacebookWebhookSetup> GetFacebookSetupAsync(string token, CancellationToken cancellationToken = default);
    Task SaveFacebookPageAsync(string pageId, string pageName, string accessToken, string token,
        CancellationToken cancellationToken = default);
    Task ConfigureWhatsAppAccountAsync(Guid id, string businessAccountId, string phoneNumberId, string token, CancellationToken cancellationToken = default);
    Task DisableWhatsAppAsync(Guid id, string token, CancellationToken cancellationToken = default);
    Task DisableFacebookAsync(string token, CancellationToken cancellationToken = default);
    Task<TikTokSetup> GetTikTokSetupAsync(string token, CancellationToken cancellationToken = default);
    Task SaveTikTokAppAsync(string appId, string appSecret, string token, CancellationToken cancellationToken = default);
    Task<TikTokAuthorization> GetTikTokAuthorizationAsync(string token, CancellationToken cancellationToken = default);
    Task DisableTikTokAsync(string token, CancellationToken cancellationToken = default);
    Task<InstagramWebhookSetup> GetInstagramSetupAsync(string token, CancellationToken cancellationToken = default);
    Task SaveInstagramAccountAsync(string accessToken, string token, CancellationToken cancellationToken = default);
    Task SaveInstagramAppSecretAsync(string appSecret, string token, CancellationToken cancellationToken = default);
    Task DisableInstagramAsync(string token, CancellationToken cancellationToken = default);
    Task<string?> GetWebhookUrlAsync(string platform, string token, CancellationToken cancellationToken = default);
    Task<List<WhatsAppConnectionInfo>> GetWhatsAppConnectionsAsync(string token, CancellationToken cancellationToken = default);
    Task<WhatsAppSetup> CreateWhatsAppSetupAsync(string name, string token, CancellationToken cancellationToken = default);
    Task<WhatsAppSetup> GetWhatsAppSetupAsync(Guid id, string token, CancellationToken cancellationToken = default);
    Task<WhatsAppConnectionInfo> GetWhatsAppStatusAsync(Guid id, string token, CancellationToken cancellationToken = default);
    Task<CatalogueSyncResult> SyncCatalogueAsync(Guid connectionId, string token, CancellationToken cancellationToken = default);
    Task<CatalogueItemsResponse> GetCatalogueProductsAsync(Guid connectionId, int page, int pageSize, string token, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>Queues a metadata refresh for the given WhatsApp connection's AI knowledge source.</summary>
    Task RefreshKnowledgeAsync(Guid connectionId, string token, CancellationToken cancellationToken = default);

    /// <summary>Queues a metadata refresh for any channel connection's AI knowledge source.</summary>
    Task RefreshConnectionKnowledgeAsync(Guid connectionId, string token, CancellationToken cancellationToken = default);
}
