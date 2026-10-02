using Microsoft.Extensions.Options;
using Parrot.Application.Realtime;
using StackExchange.Redis;

namespace Parrot.Infrastructure.Realtime;

/// <summary>
/// One sorted set per user, <c>realtime:online:{userId}</c>: each member is a
/// SignalR connection id, scored with the Unix time (ms) its session expires.
///
/// The score is the per-connection TTL. Redis expires whole keys, not set
/// members, so a connection that vanished without disconnecting would stay
/// in a plain set for as long as the user's other tabs kept it alive. Reads
/// drop every member whose score has passed, and the key itself expires once
/// the last connection stops renewing, so nothing outlives its TTL.
/// </summary>
public sealed class RedisOnlineSessionStore(
    IConnectionMultiplexer redis,
    IOptions<RedisSettings> settings,
    TimeProvider clock) : IOnlineSessionStore
{
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(settings.Value.SessionTtlSeconds);

    public static RedisKey KeyFor(string userId) => $"realtime:online:{userId}";

    public async Task RegisterAsync(string userId, string connectionId, CancellationToken cancellationToken = default)
    {
        RedisKey key = KeyFor(userId);
        long expiresAt = clock.GetUtcNow().Add(_ttl).ToUnixTimeMilliseconds();

        ITransaction transaction = redis.GetDatabase().CreateTransaction();
        _ = transaction.SortedSetAddAsync(key, connectionId, expiresAt);
        _ = transaction.KeyExpireAsync(key, _ttl);
        await transaction.ExecuteAsync();
    }

    public async Task RemoveAsync(string userId, string connectionId, CancellationToken cancellationToken = default)
    {
        await redis.GetDatabase().SortedSetRemoveAsync(KeyFor(userId), connectionId);
    }

    public async Task<IReadOnlyList<string>> GetConnectionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        RedisKey key = KeyFor(userId);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        IDatabase db = redis.GetDatabase();

        // Prune the expired members, then read the live ones; scores at or
        // before now are sessions that missed their heartbeat.
        _ = db.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, now, flags: CommandFlags.FireAndForget);
        RedisValue[] live = await db.SortedSetRangeByScoreAsync(key, now, double.PositiveInfinity, Exclude.Start);

        return live.Select(value => value.ToString()).ToArray();
    }
}
