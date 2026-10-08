using Parrot.Domain.Entities;

namespace Parrot.Application.Integrations;

// Persistence for TikTok connections. One active connection per owner.
public interface ITikTokConnectionRepository
{
    // Returns this owner's active connection, or null before setup has started.
    Task<TikTokConnection?> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default);

    // Resolves the connection named by a webhook's ?ref= value; the only routing path for inbound events.
    Task<TikTokConnection?> GetActiveByIdentifierAsync(string identifier, CancellationToken cancellationToken = default);

    // Inserts a new connection; throws ConflictException if the owner already has one.
    Task AddAsync(TikTokConnection connection, CancellationToken cancellationToken = default);

    // Marks the owner's active connection disabled; its ?ref= stops routing webhooks at once.
    Task<bool> DisableAsync(Guid id, string ownerUserId, DateTime disabledAt, CancellationToken cancellationToken = default);

    // Saves the authorized TikTok Business Account and its encrypted tokens; throws ConflictException
    // if the connection is unavailable or the account is already connected elsewhere.
    Task SaveAccountAsync(Guid id, string ownerUserId, string tikTokOpenId, string displayName,
        string encryptedAccessToken, DateTime accessTokenExpiresAt,
        string encryptedRefreshToken, DateTime refreshTokenExpiresAt,
        CancellationToken cancellationToken = default);
}
