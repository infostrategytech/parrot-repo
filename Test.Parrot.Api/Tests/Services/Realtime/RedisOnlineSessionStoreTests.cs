using Microsoft.Extensions.Options;
using Parrot.Application.Realtime;
using Parrot.Infrastructure.Realtime;
using StackExchange.Redis;

namespace Test.Parrot.Api.Tests.Services.Realtime;

public class RedisOnlineSessionStoreTests : IAsyncLifetime
{
    public sealed class RedisFactAttribute : FactAttribute
    {
        public RedisFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARROT_TEST_REDIS") is null)
                Skip = "Set PARROT_TEST_REDIS to a disposable local Redis, e.g. localhost:16379 from docker-compose.local.yml.";
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private const int TtlSeconds = 60;

    // Every test gets its own user, so tests never see each other's sessions.
    private readonly string _user = "test-user-" + Guid.NewGuid().ToString("N");
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private ConnectionMultiplexer? _redis;

    public async Task InitializeAsync()
    {
        string? connection = Environment.GetEnvironmentVariable("PARROT_TEST_REDIS");
        if (connection is not null) _redis = await ConnectionMultiplexer.ConnectAsync(connection);
    }

    public async Task DisposeAsync()
    {
        if (_redis is null) return;
        await _redis.GetDatabase().KeyDeleteAsync(RedisOnlineSessionStore.KeyFor(_user));
        await _redis.DisposeAsync();
    }

    private RedisOnlineSessionStore Store() => new(
        _redis!, Options.Create(new RedisSettings { SessionTtlSeconds = TtlSeconds }), _clock);

    [RedisFact]
    public async Task Register_MakesTheConnectionOnline()
    {
        await Store().RegisterAsync(_user, "conn-1");

        Assert.Equal(["conn-1"], await Store().GetConnectionsAsync(_user));
    }

    [RedisFact]
    public async Task AUserWithNoSessionIsOffline()
    {
        Assert.Empty(await Store().GetConnectionsAsync(_user));
    }

    [RedisFact]
    public async Task OneUserCanHaveSeveralConnections()
    {
        await Store().RegisterAsync(_user, "laptop");
        await Store().RegisterAsync(_user, "phone");

        Assert.Equal(["laptop", "phone"], (await Store().GetConnectionsAsync(_user)).Order());
    }

    [RedisFact]
    public async Task Remove_TakesOnlyThatConnectionOffline()
    {
        await Store().RegisterAsync(_user, "laptop");
        await Store().RegisterAsync(_user, "phone");

        await Store().RemoveAsync(_user, "laptop");

        Assert.Equal(["phone"], await Store().GetConnectionsAsync(_user));
    }

    [RedisFact]
    public async Task Remove_OfAnUnknownConnectionDoesNothing()
    {
        await Store().RemoveAsync(_user, "never-registered");

        Assert.Empty(await Store().GetConnectionsAsync(_user));
    }

    [RedisFact]
    public async Task ASessionWithoutAHeartbeatExpires()
    {
        await Store().RegisterAsync(_user, "conn-1");

        _clock.Now = _clock.Now.AddSeconds(TtlSeconds + 1);

        Assert.Empty(await Store().GetConnectionsAsync(_user));
    }

    [RedisFact]
    public async Task AHeartbeatKeepsTheSessionAlivePastItsFirstTtl()
    {
        await Store().RegisterAsync(_user, "conn-1");

        _clock.Now = _clock.Now.AddSeconds(TtlSeconds - 10);
        await Store().RegisterAsync(_user, "conn-1");
        _clock.Now = _clock.Now.AddSeconds(TtlSeconds - 10);

        Assert.Equal(["conn-1"], await Store().GetConnectionsAsync(_user));
    }

    // A tab that died without disconnecting must not linger while another tab
    // keeps the user's key alive.
    [RedisFact]
    public async Task AStaleConnectionExpiresWhileAnotherKeepsRenewing()
    {
        await Store().RegisterAsync(_user, "dead-tab");
        _clock.Now = _clock.Now.AddSeconds(TtlSeconds - 10);
        await Store().RegisterAsync(_user, "live-tab");

        _clock.Now = _clock.Now.AddSeconds(20);

        Assert.Equal(["live-tab"], await Store().GetConnectionsAsync(_user));
    }

    [RedisFact]
    public async Task TheUserKeyExpiresInRedisOnceRenewalsStop()
    {
        await Store().RegisterAsync(_user, "conn-1");

        TimeSpan? ttl = await _redis!.GetDatabase().KeyTimeToLiveAsync(RedisOnlineSessionStore.KeyFor(_user));

        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(TtlSeconds));
    }

    // Redis holds routing only: the user's key is the one thing written, and
    // it holds connection ids, never message content.
    [RedisFact]
    public async Task OnlyConnectionIdsAreStored()
    {
        await Store().RegisterAsync(_user, "conn-1");
        IDatabase db = _redis!.GetDatabase();

        Assert.Equal(RedisType.SortedSet, await db.KeyTypeAsync(RedisOnlineSessionStore.KeyFor(_user)));
        Assert.Equal(["conn-1"], (await db.SortedSetRangeByRankAsync(RedisOnlineSessionStore.KeyFor(_user))).Select(v => v.ToString()));
    }

    // After a Redis restart the store reads as empty, and the next heartbeat
    // brings the session back without any recovery step.
    [RedisFact]
    public async Task AHeartbeatAfterRedisWasEmptiedRegistersAgain()
    {
        await Store().RegisterAsync(_user, "conn-1");
        await _redis!.GetDatabase().KeyDeleteAsync(RedisOnlineSessionStore.KeyFor(_user));
        Assert.Empty(await Store().GetConnectionsAsync(_user));

        await Store().RegisterAsync(_user, "conn-1");

        Assert.Equal(["conn-1"], await Store().GetConnectionsAsync(_user));
    }
}
