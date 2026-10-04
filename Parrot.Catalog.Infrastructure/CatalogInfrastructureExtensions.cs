using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Parrot.Catalog.Application.Caching;
using Parrot.Catalog.Application.Documents;
using Parrot.Catalog.Application.Events;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Infrastructure.Caching;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Catalog.Infrastructure.Events;
using Parrot.Catalog.Infrastructure.Services;
using Parrot.Catalog.Infrastructure.Tenancy;
using StackExchange.Redis;

namespace Parrot.Catalog.Infrastructure;

public static class CatalogInfrastructureExtensions
{
    /// <summary>Everything the write and read APIs need: both contexts, registries, services, cache.</summary>
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CatalogOptions>(configuration.GetSection(CatalogOptions.SectionName));
        CatalogOptions options = configuration.GetSection(CatalogOptions.SectionName).Get<CatalogOptions>() ?? new CatalogOptions();
        Require(options.ConnectionStrings.Writer, "Catalog:ConnectionStrings:Writer");
        Require(options.ConnectionStrings.Reader, "Catalog:ConnectionStrings:Reader");
        TenantConnectionInterceptor.AssertSafe(options.ConnectionStrings.Writer, "Writer");
        TenantConnectionInterceptor.AssertSafe(options.ConnectionStrings.Reader, "Reader");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CatalogMetrics>();
        services.AddMemoryCache();
        services.TryAddScoped<TenantContext>();
        services.TryAddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantConnectionInterceptor>();

        services.AddDbContext<CatalogDbContext>((sp, o) => o
            .UseNpgsql(options.ConnectionStrings.Writer, n => n.MigrationsHistoryTable("__ef_migrations_history", ModelConventions.Schema))
            .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));
        services.AddDbContext<CatalogReadDbContext>((sp, o) => o
            .UseNpgsql(options.ConnectionStrings.Reader)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));

        services.AddScoped<IWriteSchemaRegistry, WriteSchemaRegistry>();
        services.AddScoped<IReadSchemaRegistry, ReadSchemaRegistry>();
        services.AddSingleton<ICanonicalDocumentRenderer, CanonicalDocumentRenderer>();
        services.AddScoped<DerivedDataRebuilder>();

        services.AddScoped<ICatalogAdminService, CatalogAdminService>();
        services.AddScoped<IProductTypeAdminService, ProductTypeAdminService>();
        services.AddScoped<ICategoryAdminService, CategoryAdminService>();
        services.AddScoped<IProductAdminService, ProductAdminService>();
        services.AddScoped<IOfferAdminService, OfferAdminService>();
        services.AddScoped<IAvailabilityAdminService, AvailabilityAdminService>();
        services.AddScoped<IRelationshipAdminService, RelationshipAdminService>();
        services.AddScoped<IMediaAdminService, MediaAdminService>();
        services.AddScoped<ITypeVersionMigrator, TypeVersionMigrator>();
        services.AddScoped<ICatalogReadService, CatalogReadService>();
        services.AddScoped<IAiToolService, AiToolService>();

        AddReadCache(services, options);
        return services;
    }

    /// <summary>The relay's dependencies: the relay context, the bus publisher, the cache (for invalidation) and the handlers.</summary>
    public static IServiceCollection AddCatalogRelay(this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        services.Configure<CatalogOptions>(configuration.GetSection(CatalogOptions.SectionName));
        CatalogOptions options = configuration.GetSection(CatalogOptions.SectionName).Get<CatalogOptions>() ?? new CatalogOptions();
        Require(options.ConnectionStrings.Relay, "Catalog:ConnectionStrings:Relay");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CatalogMetrics>();
        services.AddMemoryCache();
        services.AddDbContext<RelayDbContext>(o => o.UseNpgsql(options.ConnectionStrings.Relay));

        if (string.Equals(options.Relay.Bus, "Null", StringComparison.OrdinalIgnoreCase))
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException("Catalog:Relay:Bus=Null drops every event and is only allowed in Development.");
            }

            services.TryAddSingleton<IEventBusPublisher, NullEventBusPublisher>();
        }
        else
        {
            services.TryAddSingleton<IEventBusPublisher, KafkaEventBusPublisher>();
        }

        services.AddScoped<OutboxRelay>();
        services.AddScoped<CacheInvalidationHandler>();
        AddReadCache(services, options);
        return services;
    }

    private static void AddReadCache(IServiceCollection services, CatalogOptions options)
    {
        if (string.Equals(options.Cache.Provider, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            Require(options.Cache.RedisConnectionString, "Catalog:Cache:RedisConnectionString");
            services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options.Cache.RedisConnectionString!));
            services.TryAddSingleton<ICatalogReadCache, RedisCatalogReadCache>();
        }
        else
        {
            services.TryAddSingleton<ICatalogReadCache, MemoryCatalogReadCache>();
        }
    }

    private static void Require(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} must be configured.");
        }
    }
}
