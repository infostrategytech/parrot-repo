using Parrot.Domain.Entities;

namespace Parrot.Application.Integrations;

public interface IWhatsAppConnectionRepository
{
    Task SaveAccessTokenAsync(Guid id, string ownerUserId, string encryptedToken);
    Task SaveAppSecretAsync(Guid id, string ownerUserId, string encryptedSecret);
    Task ConfigureAccountAsync(Guid id, string ownerUserId, string businessAccountId, string phoneNumberId);
    Task AddAsync(WhatsAppConnection connection);
    Task<IReadOnlyList<WhatsAppConnection>> GetByOwnerAsync(string ownerUserId);
    Task<WhatsAppConnection?> GetByOwnerAndIdAsync(string ownerUserId, Guid id);
    Task<WhatsAppConnection?> GetActiveByIdentifierAsync(string identifier);
    Task<bool> MarkWebhookVerifiedAsync(Guid id, DateTime verifiedAt);
    Task DisableAsync(Guid id, string ownerUserId, DateTime disabledAt);
}
