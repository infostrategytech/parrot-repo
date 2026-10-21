using Parrot.Domain.Entities;

namespace Parrot.Application.Integrations;

// Persistence for TikTok connections. One active connection per owner.
public interface ITikTokConnectionRepository
{
    // Returns this owner's active connection, or null before setup has started.
    Task<TikTokConnection?> GetByOwnerAsync(string workspaceId, CancellationToken cancellationToken = default);

    // Resolves the connection named by the reference a webhook's URL ends in; the only routing path for inbound events.
    Task<TikTokConnection?> GetActiveByIdentifierAsync(string identifier, CancellationToken cancellationToken = default);

    // Inserts a new connection; throws ConflictException if the owner already has one.
    Task AddAsync(TikTokConnection connection, CancellationToken cancellationToken = default);

    // Marks the owner's active connection disabled; its webhook URL stops routing at once.
    Task<bool> DisableAsync(Guid id, string workspaceId, DateTime disabledAt, CancellationToken cancellationToken = default);

    // Replaces the tokens after a refresh, only if the stored refresh token is still the one that was used,
    // so two sends refreshing together cannot overwrite each other's rotated token. False: it changed first.
    Task<bool> SaveRefreshedTokensAsync(Guid id, string expectedEncryptedRefreshToken,
        string encryptedAccessToken, DateTime accessTokenExpiresAt,
        string encryptedRefreshToken, DateTime refreshTokenExpiresAt,
        CancellationToken cancellationToken = default);

    // Saves the authorized TikTok Business Account and its encrypted tokens; throws ConflictException
    // if the connection is unavailable or the account is already connected elsewhere.
    Task SaveAccountAsync(Guid id, string workspaceId, string tikTokOpenId, string displayName,
        string encryptedAccessToken, DateTime accessTokenExpiresAt,
        string encryptedRefreshToken, DateTime refreshTokenExpiresAt,
        CancellationToken cancellationToken = default);
}
