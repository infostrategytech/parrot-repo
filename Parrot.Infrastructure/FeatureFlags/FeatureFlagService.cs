using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Parrot.Application.FeatureFlags;
using Parrot.Application.Logging;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.FeatureFlags;

// Resolution order: the FeatureFlags row, then the FeatureFlags:{key} setting,
// then the default the caller passes. Rows are cached briefly, so a flip in
// the database shows up within half a minute without a query per read. A
// database fault (the table not migrated yet, say) falls through to the
// configured default rather than taking sign-up down with it.
public sealed class FeatureFlagService : IFeatureFlagService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly IAppLogger<FeatureFlagService> _logger;

    public FeatureFlagService(
        ApplicationDbContext db,
        IMemoryCache cache,
        IConfiguration configuration,
        IAppLogger<FeatureFlagService> logger)
    {
        _db = db;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> IsEnabledAsync(string key, bool defaultValue, CancellationToken cancellationToken = default)
    {
        bool configured = _configuration.GetValue("FeatureFlags:" + key, defaultValue);
        string cacheKey = "feature-flag:" + key;

        if (_cache.TryGetValue(cacheKey, out Lookup? cached) && cached is not null)
        {
            return cached.Stored ?? configured;
        }

        bool? stored;
        try
        {
            stored = await _db.FeatureFlags
                .AsNoTracking()
                .Where(f => f.Key == key)
                .Select(f => (bool?)f.Enabled)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read feature flag {Key}; using the configured default ({Default})", key, configured);
            return configured;
        }

        _cache.Set(cacheKey, new Lookup(stored), CacheFor);
        return stored ?? configured;
    }

    // Wrapped so "no row" can sit in the cache as a value rather than a null.
    private sealed record Lookup(bool? Stored);
}
