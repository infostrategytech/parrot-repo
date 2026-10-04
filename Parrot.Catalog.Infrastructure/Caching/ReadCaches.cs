using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Parrot.Catalog.Application.Caching;
using StackExchange.Redis;

namespace Parrot.Catalog.Infrastructure.Caching;

/// <summary>Redis-backed read cache with per-tenant generations (ADR-006).</summary>
public sealed class RedisCatalogReadCache : ICatalogReadCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly CatalogCacheOptions _options;

    public RedisCatalogReadCache(IConnectionMultiplexer redis, IOptions<CatalogOptions> options)
    {
        _redis = redis;
        _options = options.Value.Cache;
    }

    public async Task<CachedResponse?> GetAsync(Guid tenantId, string resource, string requestHash, CancellationToken ct)
    {
        IDatabase db = _redis.GetDatabase();
        long generation = await GenerationAsync(db, tenantId);
        RedisValue value = await db.StringGetAsync(Key(tenantId, generation, resource, requestHash));
        return value.IsNullOrEmpty ? null : Decode((byte[])value!);
    }

    public async Task SetAsync(Guid tenantId, string resource, string requestHash, CachedResponse value, CancellationToken ct)
    {
        IDatabase db = _redis.GetDatabase();
        long generation = await GenerationAsync(db, tenantId);
        await db.StringSetAsync(Key(tenantId, generation, resource, requestHash), Encode(value), TimeSpan.FromSeconds(_options.TtlSeconds));
    }

    public Task BumpGenerationAsync(Guid tenantId, CancellationToken ct)
        => _redis.GetDatabase().StringIncrementAsync(GenerationKey(tenantId));

    private async Task<long> GenerationAsync(IDatabase db, Guid tenantId)
    {
        RedisValue value = await db.StringGetAsync(GenerationKey(tenantId));
        return value.IsNullOrEmpty ? 0 : (long)value;
    }

    private string GenerationKey(Guid tenantId) => $"{_options.KeyPrefix}:{tenantId:N}:gen";

    private string Key(Guid tenantId, long generation, string resource, string requestHash)
        => $"{_options.KeyPrefix}:{tenantId:N}:g{generation}:{resource}:{requestHash}";

    // etag|storedAtTicks|body
    private static byte[] Encode(CachedResponse value)
    {
        byte[] header = Encoding.UTF8.GetBytes($"{value.ETag}|{value.StoredAt.Ticks}|");
        byte[] result = new byte[header.Length + value.Body.Length];
        header.CopyTo(result, 0);
        value.Body.CopyTo(result, header.Length);
        return result;
    }

    private static CachedResponse? Decode(byte[] raw)
    {
        int first = Array.IndexOf(raw, (byte)'|');
        if (first < 0)
        {
            return null;
        }

        int second = Array.IndexOf(raw, (byte)'|', first + 1);
        if (second < 0)
        {
            return null;
        }

        string etag = Encoding.UTF8.GetString(raw, 0, first);
        if (!long.TryParse(Encoding.UTF8.GetString(raw, first + 1, second - first - 1), out long ticks))
        {
            return null;
        }

        return new CachedResponse(raw[(second + 1)..], etag, new DateTime(ticks, DateTimeKind.Utc));
    }
}

/// <summary>Process-local cache with the same semantics. For tests and single-instance development.</summary>
public sealed class MemoryCatalogReadCache : ICatalogReadCache
{
    private readonly IMemoryCache _cache;
    private readonly CatalogCacheOptions _options;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _generations = new();

    public MemoryCatalogReadCache(IMemoryCache cache, IOptions<CatalogOptions> options)
    {
        _cache = cache;
        _options = options.Value.Cache;
    }

    public Task<CachedResponse?> GetAsync(Guid tenantId, string resource, string requestHash, CancellationToken ct)
        => Task.FromResult(_cache.TryGetValue(Key(tenantId, resource, requestHash), out CachedResponse? value) ? value : null);

    public Task SetAsync(Guid tenantId, string resource, string requestHash, CachedResponse value, CancellationToken ct)
    {
        _cache.Set(Key(tenantId, resource, requestHash), value, TimeSpan.FromSeconds(_options.TtlSeconds));
        return Task.CompletedTask;
    }

    public Task BumpGenerationAsync(Guid tenantId, CancellationToken ct)
    {
        _generations.AddOrUpdate(tenantId, 1, (_, g) => g + 1);
        return Task.CompletedTask;
    }

    private string Key(Guid tenantId, string resource, string requestHash)
        => $"{_options.KeyPrefix}:{tenantId:N}:g{_generations.GetValueOrDefault(tenantId)}:{resource}:{requestHash}";
}
