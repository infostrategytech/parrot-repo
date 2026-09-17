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
            $"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = {connection.OwnerUserId} FOR UPDATE");
        if (await _db.WhatsAppConnections.AnyAsync(c => c.OwnerUserId == connection.OwnerUserId))
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

    public async Task<IReadOnlyList<WhatsAppConnection>> GetByOwnerAsync(string ownerUserId) =>
        await _db.WhatsAppConnections.AsNoTracking().Where(c => c.OwnerUserId == ownerUserId)
            .OrderByDescending(c => c.CreatedAt).ToListAsync();

    public Task<WhatsAppConnection?> GetByOwnerAndIdAsync(string ownerUserId, Guid id) =>
        _db.WhatsAppConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == ownerUserId);

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

    public async Task DisableAsync(Guid id, string ownerUserId, DateTime disabledAt)
    {
        await _db.WhatsAppConnections
            .Where(c => c.Id == id && c.OwnerUserId == ownerUserId && c.DisabledAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.DisabledAt, disabledAt)
                .SetProperty(c => c.UpdatedAt, disabledAt));
    }
}
