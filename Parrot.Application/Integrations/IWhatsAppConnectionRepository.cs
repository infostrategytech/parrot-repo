using Parrot.Domain.Entities;

namespace Parrot.Application.Integrations;

public interface IWhatsAppConnectionRepository
{
    Task SaveAccessTokenAsync(Guid id, string workspaceId, string encryptedToken);
    Task SaveAppSecretAsync(Guid id, string workspaceId, string encryptedSecret);
    Task ConfigureAccountAsync(Guid id, string workspaceId, string businessAccountId, string phoneNumberId);
    Task AddAsync(WhatsAppConnection connection);
    Task<IReadOnlyList<WhatsAppConnection>> GetByOwnerAsync(string workspaceId);
    Task<WhatsAppConnection?> GetByOwnerAndIdAsync(string workspaceId, Guid id);
    Task<WhatsAppConnection?> GetActiveByIdentifierAsync(string identifier);
    Task<bool> MarkWebhookVerifiedAsync(Guid id, DateTime verifiedAt);
    Task DisableAsync(Guid id, string workspaceId, DateTime disabledAt);
}
