using Parrot.Domain.Entities;

namespace Parrot.Application.Integrations;

public interface IWhatsAppConnectionRepository
{
    Task AddAsync(WhatsAppConnection connection);
    Task<IReadOnlyList<WhatsAppConnection>> GetByOwnerAsync(string ownerUserId);
    Task<WhatsAppConnection?> GetByOwnerAndIdAsync(string ownerUserId, Guid id);
    Task<WhatsAppConnection?> GetActiveByIdentifierAsync(string identifier);
    Task<bool> MarkWebhookVerifiedAsync(Guid id, DateTime verifiedAt);
    Task DisableAsync(Guid id, string ownerUserId, DateTime disabledAt);
}
