using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

// EF Core store for Instagram connections; also serves ?ref= webhook routing.
public sealed class InstagramConnectionRepository(ApplicationDbContext db) : IInstagramConnectionRepository
{
    public async Task<InstagramConnection?> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default) =>
        await db.InstagramConnections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.OwnerUserId == ownerUserId && c.DisabledAt == null, cancellationToken);

    public async Task<InstagramConnection?> GetActiveByIdentifierAsync(string identifier, CancellationToken cancellationToken = default) =>
        await db.InstagramConnections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.WebhookIdentifier == identifier && c.DisabledAt == null, cancellationToken);

    public async Task AddAsync(InstagramConnection connection, CancellationToken cancellationToken = default)
    {
        db.InstagramConnections.Add(connection);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("This user already has an Instagram connection.");
        }
    }

    public async Task<bool> MarkVerifiedAsync(Guid id, DateTime verifiedAt, CancellationToken cancellationToken = default) =>
        await db.InstagramConnections.Where(c => c.Id == id && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.WebhookVerifiedAt, c => c.WebhookVerifiedAt ?? verifiedAt)
                .SetProperty(c => c.UpdatedAt, verifiedAt), cancellationToken) == 1;

    // One statement; the WHERE clause enforces ownership and verification.
    public async Task SaveAccountAsync(Guid id, string ownerUserId, string accountId, string username, string encryptedAccessToken,
        DateTime expiresAt, CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        try
        {
            int updated = await db.InstagramConnections
                .Where(c => c.Id == id && c.OwnerUserId == ownerUserId && c.DisabledAt == null && c.WebhookVerifiedAt != null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(c => c.AccountId, accountId)
                    .SetProperty(c => c.Username, username)
                    .SetProperty(c => c.AccessTokenEncrypted, encryptedAccessToken)
                    .SetProperty(c => c.AccessTokenRenewedAt, now)
                    .SetProperty(c => c.AccessTokenExpiresAt, expiresAt)
                    .SetProperty(c => c.AccessTokenRejectedAt, (DateTime?)null)
                    .SetProperty(c => c.UpdatedAt, now), cancellationToken);
            if (updated == 0) throw new ConflictException("Instagram webhook verification is required before saving the account.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConflictException("This Instagram account is already connected to another Parrot account.");
        }
    }

    public async Task<IReadOnlyList<InstagramConnection>> GetDueForTokenRenewalAsync(DateTime renewBefore, int take,
        CancellationToken cancellationToken = default) =>
        await db.InstagramConnections.AsNoTracking()
            .Where(c => c.DisabledAt == null && c.AccessTokenEncrypted != null && c.AccessTokenRejectedAt == null
                && (c.AccessTokenRenewedAt ?? c.UpdatedAt) <= renewBefore)
            .OrderBy(c => c.AccessTokenRenewedAt ?? c.UpdatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<bool> SaveRenewedTokenAsync(Guid id, string previousEncryptedToken, string encryptedAccessToken, DateTime renewedAt,
        DateTime expiresAt, CancellationToken cancellationToken = default) =>
        await db.InstagramConnections
            .Where(c => c.Id == id && c.DisabledAt == null && c.AccessTokenEncrypted == previousEncryptedToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.AccessTokenEncrypted, encryptedAccessToken)
                .SetProperty(c => c.AccessTokenRenewedAt, renewedAt)
                .SetProperty(c => c.AccessTokenExpiresAt, expiresAt)
                .SetProperty(c => c.UpdatedAt, renewedAt), cancellationToken) == 1;

    // Only the token that was refused: one pasted meanwhile is not marked.
    public async Task MarkTokenRejectedAsync(Guid id, string encryptedAccessToken, DateTime rejectedAt, CancellationToken cancellationToken = default) =>
        await db.InstagramConnections
            .Where(c => c.Id == id && c.AccessTokenEncrypted == encryptedAccessToken && c.AccessTokenRejectedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.AccessTokenRejectedAt, rejectedAt)
                .SetProperty(c => c.UpdatedAt, rejectedAt), cancellationToken);

    public async Task SaveAppSecretAsync(Guid id, string ownerUserId, string encryptedSecret, CancellationToken cancellationToken = default)
    {
        int updated = await db.InstagramConnections
            .Where(c => c.Id == id && c.OwnerUserId == ownerUserId && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.AppSecretEncrypted, encryptedSecret)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken);
        if (updated == 0) throw new ConflictException("This Instagram connection is unavailable.");
    }

    public Task<bool> IsOwnedByAsync(Guid id, string ownerUserId, CancellationToken cancellationToken = default) =>
        db.InstagramConnections.AnyAsync(c => c.Id == id && c.OwnerUserId == ownerUserId, cancellationToken);

    public async Task<bool> DisableAsync(Guid id, string ownerUserId, DateTime disabledAt, CancellationToken cancellationToken = default) =>
        await db.InstagramConnections
            .Where(c => c.Id == id && c.OwnerUserId == ownerUserId && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.DisabledAt, disabledAt)
                .SetProperty(c => c.UpdatedAt, disabledAt), cancellationToken) == 1;
}
