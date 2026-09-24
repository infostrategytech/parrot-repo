using Parrot.Domain.Entities;

namespace Parrot.Application.Integrations;

// Persistence for Facebook connections. One active connection per owner.
public interface IFacebookConnectionRepository
{
    // Returns this owner's active connection, or null before setup has started.
    Task<FacebookConnection?> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default);

    // Resolves the connection that owns an inbound Page ID; used to route webhook events.
    Task<FacebookConnection?> GetByPageIdAsync(string pageId, CancellationToken cancellationToken = default);

    // Inserts a new connection; throws ConflictException if the owner already has one.
    Task AddAsync(FacebookConnection connection, CancellationToken cancellationToken = default);

    // Stamps the first successful Meta webhook challenge; later challenges keep the original time.
    Task<bool> MarkVerifiedAsync(Guid id, DateTime verifiedAt, CancellationToken cancellationToken = default);

    // Saves the chosen Page and its encrypted token; requires the webhook to be verified first.
    Task SavePageAsync(Guid id, string ownerUserId, string pageId, string pageName, string encryptedAccessToken,
        CancellationToken cancellationToken = default);
}
