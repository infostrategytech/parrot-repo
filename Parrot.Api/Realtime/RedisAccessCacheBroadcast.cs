using Microsoft.Extensions.Caching.Memory;
using Parrot.Application.Teams;
using StackExchange.Redis;

namespace Parrot.Api.Realtime;

/// <summary>
/// Access answers are cached per instance for a few seconds. When one instance
/// forgets an answer (a member removed, their access or role changed), this
/// tells every instance, itself included, to forget it too, over a Redis
/// channel. Fire and forget: if Redis is down, the answers still expire on
/// their own within <see cref="WorkspaceAccessService.Lifetime"/>.
/// </summary>
public sealed class RedisAccessCacheBroadcast(
    IConnectionMultiplexer redis,
    IMemoryCache cache,
    ILogger<RedisAccessCacheBroadcast> logger) : IAccessCacheBroadcast, IHostedService
{
    public static readonly RedisChannel Channel = RedisChannel.Literal("parrot:workspace-access:forget");

    private int _subscribed;

    public void Publish(IReadOnlyList<string> cacheKeys)
    {
        try
        {
            redis.GetSubscriber().Publish(Channel, string.Join('\n', cacheKeys), CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "access_cache.broadcast_failed Keys={Keys}; other instances catch up as their answers expire", cacheKeys.Count);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Redis may be down at boot: try again whenever the connection comes back.
        redis.ConnectionRestored += (_, _) => _ = SubscribeAsync();
        await SubscribeAsync();
    }

    private async Task SubscribeAsync()
    {
        if (Volatile.Read(ref _subscribed) == 1)
        {
            return;
        }

        try
        {
            await redis.GetSubscriber().SubscribeAsync(Channel, (_, message) => Receive(message));
            Interlocked.Exchange(ref _subscribed, 1);
        }
        catch (Exception ex)
        {
            // Meanwhile this instance still forgets its own answers and lets the rest expire.
            logger.LogWarning(ex, "access_cache.subscribe_failed; retrying when Redis is back, cached access expires on its own");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Forgets the keys in one message. Anything that is not an access key is ignored.</summary>
    public void Receive(RedisValue message)
    {
        foreach (string key in ((string?)message ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (key.StartsWith(WorkspaceAccessService.KeyPrefix, StringComparison.Ordinal))
            {
                cache.Remove(key);
            }
        }
    }
}
