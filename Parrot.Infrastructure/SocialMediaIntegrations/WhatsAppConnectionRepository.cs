using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

public class WhatsAppConnectionRepository : IWhatsAppConnectionRepository
{
    private readonly ApplicationDbContext _db;

    public WhatsAppConnectionRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(WhatsAppConnection connection)
    {
        // Lock the owner row so requests from multiple tabs or API replicas cannot create duplicates.
        // Existing connections are preserved; this also works for accounts created before the one-connection rule.
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = {connection.WorkspaceId} FOR UPDATE");
        if (await _db.WhatsAppConnections.AnyAsync(c => c.WorkspaceId == connection.WorkspaceId))
        {
            throw new ConflictException("This user already has a WhatsApp connection.");
        }
        _db.WhatsAppConnections.Add(connection);
        try
        {
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_WhatsAppConnections_PhoneNumberId",
        })
        {
            throw new ConflictException("This phone number already has an active WhatsApp connection.");
        }
    }

    public async Task<IReadOnlyList<WhatsAppConnection>> GetByOwnerAsync(string workspaceId) =>
        await _db.WhatsAppConnections.AsNoTracking().Where(c => c.WorkspaceId == workspaceId)
            .OrderByDescending(c => c.CreatedAt).ToListAsync();

    public Task<WhatsAppConnection?> GetByOwnerAndIdAsync(string workspaceId, Guid id) =>
        _db.WhatsAppConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == workspaceId);

    public Task<WhatsAppConnection?> GetActiveByIdentifierAsync(string identifier) =>
        _db.WhatsAppConnections.AsNoTracking().SingleOrDefaultAsync(c => c.WebhookIdentifier == identifier && c.DisabledAt == null);

    public async Task<bool> MarkWebhookVerifiedAsync(Guid id, DateTime verifiedAt)
    {
        // Preserve the first verification time on repeated challenges; recheck disabled state atomically.
        int updated = await _db.WhatsAppConnections
            .Where(c => c.Id == id && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.UpdatedAt, c => c.WebhookVerifiedAt == null ? verifiedAt : c.UpdatedAt)
                .SetProperty(c => c.WebhookVerifiedAt, c => c.WebhookVerifiedAt ?? verifiedAt));
        return updated == 1;
    }

    public async Task DisableAsync(Guid id, string workspaceId, DateTime disabledAt)
    {
        await _db.WhatsAppConnections
            .Where(c => c.Id == id && c.WorkspaceId == workspaceId && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.DisabledAt, disabledAt)
                .SetProperty(c => c.UpdatedAt, disabledAt));
    }

    public async Task SaveAccessTokenAsync(Guid id, string workspaceId, string encryptedToken)
    {
        int count = await _db.WhatsAppConnections
            .Where(c => c.Id == id && c.WorkspaceId == workspaceId && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.AccessTokenEncrypted, encryptedToken)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow));
        if (count == 0) throw new ConflictException("This WhatsApp connection is unavailable.");
    }

    public async Task SaveAppSecretAsync(Guid id, string workspaceId, string encryptedSecret)
    {
        int count = await _db.WhatsAppConnections
            .Where(c => c.Id == id && c.WorkspaceId == workspaceId && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.AppSecretEncrypted, encryptedSecret)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow));
        if (count == 0) throw new ConflictException("This WhatsApp connection is unavailable.");
    }

    public async Task ConfigureAccountAsync(Guid id, string workspaceId, string businessAccountId, string phoneNumberId)
    {
        try
        {
            int count = await _db.WhatsAppConnections
                .Where(c => c.Id == id && c.WorkspaceId == workspaceId && c.DisabledAt == null
                    && c.FirstMessageReceivedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(c => c.WhatsAppBusinessAccountId, businessAccountId)
                    .SetProperty(c => c.PhoneNumberId, phoneNumberId)
                    .SetProperty(c => c.UpdatedAt, DateTime.UtcNow));
            if (count == 0)
                throw new ConflictException("Account details cannot change after receiving a message or disabling the connection.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConflictException("This phone number already has an active connection.");
        }
    }
}
