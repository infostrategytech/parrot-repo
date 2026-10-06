namespace Parrot.Catalog.Application.Caching;

public sealed record CachedResponse(byte[] Body, string ETag, DateTime StoredAt);

/// <summary>
/// Tenant-prefixed read cache with per-tenant generations (ADR-006). The key the
/// caller passes is already a hash of the request; the implementation adds the
/// tenant and the current generation.
/// </summary>
public interface ICatalogReadCache
{
    Task<CachedResponse?> GetAsync(Guid tenantId, string resource, string requestHash, CancellationToken ct);

    Task SetAsync(Guid tenantId, string resource, string requestHash, CachedResponse value, CancellationToken ct);

    /// <summary>Makes every cached entry for the tenant unreachable.</summary>
    Task BumpGenerationAsync(Guid tenantId, CancellationToken ct);
}
