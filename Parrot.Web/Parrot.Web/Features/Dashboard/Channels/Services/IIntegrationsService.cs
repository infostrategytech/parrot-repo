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
    Task<string?> GetWebhookUrlAsync(string platform, string token, CancellationToken cancellationToken = default);
    Task<List<WhatsAppConnectionInfo>> GetWhatsAppConnectionsAsync(string token, CancellationToken cancellationToken = default);
    Task<WhatsAppSetup> CreateWhatsAppSetupAsync(string name, string token, CancellationToken cancellationToken = default);
    Task<WhatsAppSetup> GetWhatsAppSetupAsync(Guid id, string token, CancellationToken cancellationToken = default);
    Task<WhatsAppConnectionInfo> GetWhatsAppStatusAsync(Guid id, string token, CancellationToken cancellationToken = default);
}
