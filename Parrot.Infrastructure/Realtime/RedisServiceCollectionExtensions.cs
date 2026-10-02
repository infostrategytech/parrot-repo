using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Parrot.Application.Realtime;
using StackExchange.Redis;

namespace Parrot.Infrastructure.Realtime;

public static class RedisServiceCollectionExtensions
{
    public static IServiceCollection AddRedis(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RedisSettings>(configuration.GetSection(RedisSettings.SectionName));
        RedisSettings settings = configuration.GetSection(RedisSettings.SectionName).Get<RedisSettings>()
            ?? new RedisSettings();

        if (settings.HeartbeatIntervalSeconds <= 0 || settings.HeartbeatIntervalSeconds >= settings.SessionTtlSeconds)
        {
            throw new InvalidOperationException(
                "Redis:HeartbeatIntervalSeconds must be positive and shorter than Redis:SessionTtlSeconds, or every session expires between heartbeats.");
        }

        ConfigurationOptions options = ConfigurationOptions.Parse(settings.ConnectionString);

        // Realtime delivery is best-effort: Redis being down at boot must not
        // stop the API, and with it webhooks and message persistence. The
        // multiplexer keeps retrying in the background and recovers by itself.
        options.AbortOnConnectFail = false;

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IOnlineSessionStore, RedisOnlineSessionStore>();
        return services;
    }
}
