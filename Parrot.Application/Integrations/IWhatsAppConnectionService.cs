using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

public interface IWhatsAppConnectionService
{
    Task SaveAccessTokenAsync(string workspaceId, Guid id, WhatsAppAccessTokenRequest request);
    Task SaveAppSecretAsync(string workspaceId, Guid id, AppSecretRequest request);
    Task ConfigureAccountAsync(string workspaceId, Guid id, WhatsAppAccountRequest request);
    Task<WhatsAppConnectionSetupResponse> CreateAsync(string workspaceId, CreateWhatsAppConnectionRequest request);
    Task<IReadOnlyList<WhatsAppConnectionResponse>> GetAllAsync(string workspaceId);
    Task<WhatsAppConnectionResponse> GetByIdAsync(string workspaceId, Guid id);
    Task<WhatsAppConnectionSetupResponse> GetSetupAsync(string workspaceId, Guid id);
    Task DisableAsync(string workspaceId, Guid id);
    Task<bool> VerifyWebhookAsync(string identifier, string mode, string verifyToken);
}
