using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Parrot.Application.BusinessSources;

/// <summary>
/// Persistence for business sources. Methods that modify state do NOT call SaveChanges —
/// the caller is responsible for SaveChanges (and the outer transaction) so that source
/// mutations and snapshot writes are committed atomically.
/// </summary>
public interface IBusinessSourceRepository
{
    /// <summary>
    /// Finds the existing source for <c>(WorkspaceId, Channel, Kind, ExternalId)</c> or prepares
    /// a new one. If an existing inactive source is found, it is reactivated. The returned entity
    /// is tracked by the DbContext; the caller must call SaveChanges.
    /// </summary>
    Task<BusinessSource> UpsertAsync(BusinessSource candidate, CancellationToken ct = default);

    /// <summary>Returns all active sources for the workspace, ordered by CreatedAt ascending.</summary>
    Task<IReadOnlyList<BusinessSource>> GetActiveByWorkspaceAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>Returns all sources (active and inactive) that belong to the given connection.</summary>
    Task<IReadOnlyList<BusinessSource>> GetByConnectionAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Marks all active sources for <paramref name="connectionId"/> as inactive and
    /// updates their UpdatedAt. Does NOT call SaveChanges.
    /// </summary>
    Task DeactivateByConnectionAsync(Guid connectionId, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> sources eligible for metadata sync
    /// (Pending or RetryScheduled with NextMetadataSyncAttemptAt in the past) using
    /// FOR UPDATE SKIP LOCKED. Sets MetadataSyncStatus=Processing and MetadataSyncLockedUntil.
    /// The caller must start a transaction before calling this, then call SaveChanges
    /// to persist the lease before releasing the transaction.
    /// </summary>
    Task<IReadOnlyList<BusinessSource>> ClaimBatchForMetadataAsync(int batchSize, TimeSpan leaseDuration, CancellationToken ct = default);
}
