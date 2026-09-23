using Parrot.Domain.Entities;

namespace Parrot.Application.Integrations;

public interface IFacebookConnectionRepository
{
    Task<FacebookConnection?> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default);
    Task<FacebookConnection?> GetByPageIdAsync(string pageId, CancellationToken cancellationToken = default);
    Task AddAsync(FacebookConnection connection, CancellationToken cancellationToken = default);
    Task<bool> MarkVerifiedAsync(Guid id, DateTime verifiedAt, CancellationToken cancellationToken = default);
    Task SavePageAsync(Guid id, string ownerUserId, string pageId, string pageName, string encryptedAccessToken,
        CancellationToken cancellationToken = default);
}
