using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

// EF Core store for TikTok connections; also serves ?ref= webhook routing.
public sealed class TikTokConnectionRepository(ApplicationDbContext db) : ITikTokConnectionRepository
{
    // Reads this owner's active connection.
    public async Task<TikTokConnection?> GetByOwnerAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        return await db.TikTokConnections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.DisabledAt == null, cancellationToken);
    }

    // Indexed lookup on the webhook reference.
    public async Task<TikTokConnection?> GetActiveByIdentifierAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return await db.TikTokConnections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.WebhookIdentifier == identifier && c.DisabledAt == null, cancellationToken);
    }

    // Inserts a connection, translating the unique-index violation into a ConflictException.
    public async Task AddAsync(TikTokConnection connection, CancellationToken cancellationToken = default)
    {
        db.TikTokConnections.Add(connection);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("This user already has a TikTok connection.");
        }
    }

    public async Task<bool> DisableAsync(Guid id, string workspaceId, DateTime disabledAt,
        CancellationToken cancellationToken = default) =>
        await db.TikTokConnections
            .Where(c => c.Id == id && c.WorkspaceId == workspaceId && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.DisabledAt, disabledAt)
                .SetProperty(c => c.UpdatedAt, disabledAt), cancellationToken) == 1;

    public async Task<bool> SaveRefreshedTokensAsync(Guid id, string expectedEncryptedRefreshToken,
        string encryptedAccessToken, DateTime accessTokenExpiresAt,
        string encryptedRefreshToken, DateTime refreshTokenExpiresAt,
        CancellationToken cancellationToken = default) =>
        await db.TikTokConnections
            .Where(c => c.Id == id && c.DisabledAt == null && c.RefreshTokenEncrypted == expectedEncryptedRefreshToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.AccessTokenEncrypted, encryptedAccessToken)
                .SetProperty(c => c.AccessTokenExpiresAt, accessTokenExpiresAt)
                .SetProperty(c => c.RefreshTokenEncrypted, encryptedRefreshToken)
                .SetProperty(c => c.RefreshTokenExpiresAt, refreshTokenExpiresAt)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken) == 1;

    // Writes the account and tokens in one statement; the WHERE clause enforces ownership.
    public async Task SaveAccountAsync(Guid id, string workspaceId, string tikTokOpenId, string displayName,
        string encryptedAccessToken, DateTime accessTokenExpiresAt,
        string encryptedRefreshToken, DateTime refreshTokenExpiresAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            int updated = await db.TikTokConnections
                .Where(c => c.Id == id && c.WorkspaceId == workspaceId && c.DisabledAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(c => c.TikTokOpenId, tikTokOpenId)
                    .SetProperty(c => c.DisplayName, displayName)
                    .SetProperty(c => c.AccessTokenEncrypted, encryptedAccessToken)
                    .SetProperty(c => c.AccessTokenExpiresAt, accessTokenExpiresAt)
                    .SetProperty(c => c.RefreshTokenEncrypted, encryptedRefreshToken)
                    .SetProperty(c => c.RefreshTokenExpiresAt, refreshTokenExpiresAt)
                    .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken);
            if (updated == 0) throw new ConflictException("This TikTok connection is unavailable.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConflictException("This TikTok account is already connected to another Parrot account.");
        }
    }
}
