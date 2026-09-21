using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

public interface IWhatsAppConnectionService
{
    Task SaveAccessTokenAsync(string ownerUserId, Guid id, WhatsAppAccessTokenRequest request);
    Task ConfigureAccountAsync(string ownerUserId, Guid id, WhatsAppAccountRequest request);
    Task<WhatsAppConnectionSetupResponse> CreateAsync(string ownerUserId, CreateWhatsAppConnectionRequest request);
    Task<IReadOnlyList<WhatsAppConnectionResponse>> GetAllAsync(string ownerUserId);
    Task<WhatsAppConnectionResponse> GetByIdAsync(string ownerUserId, Guid id);
    Task<WhatsAppConnectionSetupResponse> GetSetupAsync(string ownerUserId, Guid id);
    Task DisableAsync(string ownerUserId, Guid id);
    Task<bool> VerifyWebhookAsync(string identifier, string mode, string verifyToken);
}
