using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Payments;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Payments;

public sealed class EfWorkspacePaymentSettingsRepository(IDbContextFactory<ApplicationDbContext> factory) : IWorkspacePaymentSettingsRepository
{
    public async Task<WorkspacePaymentSettings?> GetAsync(string workspaceId, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        return await db.WorkspacePaymentSettings.AsNoTracking().FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId, ct);
    }

    public async Task SaveAsync(WorkspacePaymentSettings settings, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        WorkspacePaymentSettings? row = await db.WorkspacePaymentSettings.FirstOrDefaultAsync(s => s.WorkspaceId == settings.WorkspaceId, ct);
        if (row is null)
        {
            db.WorkspacePaymentSettings.Add(settings);
        }
        else
        {
            db.Entry(row).CurrentValues.SetValues(settings);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && row is null)
        {
            // Two first saves at once: the other made the row, so this one updates it.
            await using ApplicationDbContext retry = await factory.CreateDbContextAsync(ct);
            WorkspacePaymentSettings existing = await retry.WorkspacePaymentSettings.FirstAsync(s => s.WorkspaceId == settings.WorkspaceId, ct);
            retry.Entry(existing).CurrentValues.SetValues(settings);
            await retry.SaveChangesAsync(ct);
        }
    }
}
