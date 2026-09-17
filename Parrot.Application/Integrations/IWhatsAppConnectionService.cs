using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

public interface IWhatsAppConnectionService
{
    Task<WhatsAppConnectionSetupResponse> CreateAsync(string ownerUserId, CreateWhatsAppConnectionRequest request);
    Task<IReadOnlyList<WhatsAppConnectionResponse>> GetAllAsync(string ownerUserId);
    Task<WhatsAppConnectionResponse> GetByIdAsync(string ownerUserId, Guid id);
    Task<WhatsAppConnectionSetupResponse> GetSetupAsync(string ownerUserId, Guid id);
    Task DisableAsync(string ownerUserId, Guid id);
    Task<bool> VerifyWebhookAsync(string identifier, string mode, string verifyToken);
}
