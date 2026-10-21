using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

// EF Core store for Facebook connections; also serves ?ref= webhook routing.
public sealed class FacebookConnectionRepository(ApplicationDbContext db) : IFacebookConnectionRepository
{
    // Reads this owner's active connection.
    public async Task<FacebookConnection?> GetByOwnerAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        return await db.FacebookConnections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.DisabledAt == null, cancellationToken);
    }

    // Indexed lookup on the webhook reference; replaces the decrypt-every-row verify token scan.
    public async Task<FacebookConnection?> GetActiveByIdentifierAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return await db.FacebookConnections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.WebhookIdentifier == identifier && c.DisabledAt == null, cancellationToken);
    }

    // Inserts a connection, translating the unique-index violation into a ConflictException.
    public async Task AddAsync(FacebookConnection connection, CancellationToken cancellationToken = default)
    {
        db.FacebookConnections.Add(connection);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("This user already has a Facebook connection.");
        }
    }

    // Records the first verification only, so repeat challenges from Meta are idempotent.
    public async Task<bool> MarkVerifiedAsync(Guid id, DateTime verifiedAt, CancellationToken cancellationToken = default)
    {
        return await db.FacebookConnections.Where(c => c.Id == id && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.WebhookVerifiedAt, c => c.WebhookVerifiedAt ?? verifiedAt)
                .SetProperty(c => c.UpdatedAt, verifiedAt), cancellationToken) == 1;
    }

    public Task<bool> IsOwnedByAsync(Guid id, string workspaceId, CancellationToken cancellationToken = default) =>
        db.FacebookConnections.AnyAsync(c => c.Id == id && c.WorkspaceId == workspaceId, cancellationToken);

    public async Task<bool> DisableAsync(Guid id, string workspaceId, DateTime disabledAt,
        CancellationToken cancellationToken = default) =>
        await db.FacebookConnections
            .Where(c => c.Id == id && c.WorkspaceId == workspaceId && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.DisabledAt, disabledAt)
                .SetProperty(c => c.UpdatedAt, disabledAt), cancellationToken) == 1;

    public async Task SaveAppSecretAsync(Guid id, string workspaceId, string encryptedSecret,
        CancellationToken cancellationToken = default)
    {
        int updated = await db.FacebookConnections
            .Where(c => c.Id == id && c.WorkspaceId == workspaceId && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.AppSecretEncrypted, encryptedSecret)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken);
        if (updated == 0) throw new ConflictException("This Facebook connection is unavailable.");
    }

    // Writes the Page and token in one statement; the WHERE clause enforces ownership and verification.
    public async Task SavePageAsync(Guid id, string workspaceId, string pageId, string pageName,
        string encryptedAccessToken, CancellationToken cancellationToken = default)
    {
        try
        {
            int updated = await db.FacebookConnections
                .Where(c => c.Id == id && c.WorkspaceId == workspaceId && c.DisabledAt == null && c.WebhookVerifiedAt != null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(c => c.PageId, pageId)
                    .SetProperty(c => c.PageName, pageName)
                    .SetProperty(c => c.AccessTokenEncrypted, encryptedAccessToken)
                    .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken);
            if (updated == 0) throw new ConflictException("Facebook webhook verification is required before saving the Page.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConflictException("This Facebook Page is already connected to another Parrot account.");
        }
    }
}
