using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

public sealed class FacebookConnectionRepository(ApplicationDbContext db) :
    IFacebookConnectionRepository, IFacebookPageRegistry, IFacebookVerifyTokenLookup
{
    public async Task<FacebookConnection?> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        return await db.FacebookConnections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.OwnerUserId == ownerUserId && c.DisabledAt == null, cancellationToken);
    }

    public async Task<FacebookConnection?> GetByPageIdAsync(string pageId, CancellationToken cancellationToken = default)
    {
        return await db.FacebookConnections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.PageId == pageId && c.DisabledAt == null, cancellationToken);
    }

    public async Task AddAsync(FacebookConnection connection, CancellationToken cancellationToken = default)
    {
        db.FacebookConnections.Add(connection);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("This user already has a Facebook connection.");
        }
    }

    public async Task<bool> MarkVerifiedAsync(Guid id, DateTime verifiedAt, CancellationToken cancellationToken = default)
    {
        return await db.FacebookConnections.Where(c => c.Id == id && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.WebhookVerifiedAt, c => c.WebhookVerifiedAt ?? verifiedAt)
                .SetProperty(c => c.UpdatedAt, verifiedAt), cancellationToken) == 1;
    }

    public async Task SavePageAsync(Guid id, string ownerUserId, string pageId, string pageName,
        string encryptedAccessToken, CancellationToken cancellationToken = default)
    {
        try
        {
            int updated = await db.FacebookConnections
                .Where(c => c.Id == id && c.OwnerUserId == ownerUserId && c.DisabledAt == null && c.WebhookVerifiedAt != null)
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

    public async Task<FacebookPageRegistration?> ResolveAsync(string pageId, CancellationToken cancellationToken = default)
    {
        FacebookConnection? connection = await GetByPageIdAsync(pageId, cancellationToken);
        return connection is null ? null : new FacebookPageRegistration(
            connection.Id, connection.OwnerUserId, pageId, connection.PageName ?? pageId);
    }

    public async Task<FacebookConnection?> FindByVerifyTokenAsync(string verifyToken,
        IFacebookCredentialProtector protector, CancellationToken cancellationToken = default)
    {
        List<FacebookConnection> connections = await db.FacebookConnections.AsNoTracking()
            .Where(c => c.DisabledAt == null).ToListAsync(cancellationToken);
        foreach (FacebookConnection connection in connections)
        {
            try
            {
                string expected = protector.Unprotect(connection.VerifyTokenEncrypted);
                if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(verifyToken)))
                    return connection;
            }
            catch (CryptographicException) { }
        }
        return null;
    }
}
