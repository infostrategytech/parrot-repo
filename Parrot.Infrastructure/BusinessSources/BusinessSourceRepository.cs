using Microsoft.EntityFrameworkCore;
using Parrot.Application.BusinessSources;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.BusinessSources;

public class BusinessSourceRepository : IBusinessSourceRepository
{
    private readonly ApplicationDbContext _db;

    public BusinessSourceRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<BusinessSource> UpsertAsync(BusinessSource candidate, CancellationToken ct = default)
    {
        BusinessSource? existing = await _db.BusinessSources
            .FirstOrDefaultAsync(s =>
                s.WorkspaceId == candidate.WorkspaceId &&
                s.Channel == candidate.Channel &&
                s.Kind == candidate.Kind &&
                s.ExternalId == candidate.ExternalId, ct);

        if (existing is null)
        {
            _db.BusinessSources.Add(candidate);
            return candidate;
        }

        // Reactivate the existing source and refresh mutable fields.
        existing.AuthorizationConnectionId = candidate.AuthorizationConnectionId;
        existing.DisplayName = candidate.DisplayName;
        existing.Handle = candidate.Handle;
        existing.Url = candidate.Url;
        existing.IsActive = true;
        existing.MetadataSyncStatus = MetadataSyncStatus.Pending;
        existing.NextMetadataSyncAttemptAt = null;
        existing.MetadataSyncLockedUntil = null;
        existing.UpdatedAt = candidate.UpdatedAt;
        return existing;
    }

    public async Task<IReadOnlyList<BusinessSource>> GetActiveByWorkspaceAsync(string workspaceId, CancellationToken ct = default) =>
        await _db.BusinessSources
            .Where(s => s.WorkspaceId == workspaceId && s.IsActive)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BusinessSource>> GetByConnectionAsync(Guid connectionId, CancellationToken ct = default) =>
        await _db.BusinessSources
            .Where(s => s.AuthorizationConnectionId == connectionId)
            .ToListAsync(ct);

    public async Task DeactivateByConnectionAsync(Guid connectionId, DateTime now, CancellationToken ct = default)
    {
        IReadOnlyList<BusinessSource> active = await _db.BusinessSources
            .Where(s => s.AuthorizationConnectionId == connectionId && s.IsActive)
            .ToListAsync(ct);

        foreach (BusinessSource source in active)
        {
            source.IsActive = false;
            source.UpdatedAt = now;
        }
    }

    public async Task<IReadOnlyList<BusinessSource>> ClaimBatchForMetadataAsync(int batchSize, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        // FOR UPDATE SKIP LOCKED so concurrent workers don't process the same source.
        // The caller must hold an open transaction; the lease is committed before
        // the transaction is released so the lock has effect.
        List<BusinessSource> batch = await _db.BusinessSources
            .FromSqlInterpolated($"""
                SELECT * FROM "BusinessSources"
                WHERE "IsActive" = true
                  AND "MetadataSyncStatus" IN ({(int)MetadataSyncStatus.Pending}, {(int)MetadataSyncStatus.RetryScheduled})
                  AND ("NextMetadataSyncAttemptAt" IS NULL OR "NextMetadataSyncAttemptAt" <= NOW())
                  AND ("MetadataSyncLockedUntil" IS NULL OR "MetadataSyncLockedUntil" < NOW())
                ORDER BY "CreatedAt"
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        DateTime lockedUntil = DateTime.UtcNow.Add(leaseDuration);
        foreach (BusinessSource source in batch)
        {
            source.MetadataSyncStatus = MetadataSyncStatus.Processing;
            source.MetadataSyncLockedUntil = lockedUntil;
            source.LastMetadataSyncAttemptAt = DateTime.UtcNow;
        }

        return batch;
    }
}
