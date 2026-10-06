using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Infrastructure;
using Parrot.Catalog.Infrastructure.Data;

namespace Parrot.Catalog.Tools.Commands;

internal static class Hosting
{
    /// <summary>A service provider with the catalog services wired exactly as the API wires them.</summary>
    public static ServiceProvider Services(IConfiguration configuration)
    {
        ServiceCollection services = new ServiceCollection();
        services.AddLogging(l => l.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddCatalogInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A DI scope whose tenant context is established. TenantContext is scoped, so it must
    /// be set on the very scope the command runs in; RLS matches nothing otherwise.
    /// </summary>
    public static IServiceScope TenantScope(ServiceProvider provider, Guid tenantId, string actor = "tools")
    {
        IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Establish(tenantId, actor, CatalogScopes.All, $"tools-{Guid.NewGuid():N}");
        return scope;
    }

    public static CatalogDbContext Migrator(IConfiguration configuration)
    {
        string connection = configuration["Catalog:ConnectionStrings:Migrator"]
            ?? throw new InvalidOperationException("Catalog:ConnectionStrings:Migrator must be configured.");
        DbContextOptions<CatalogDbContext> options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(connection, n => n.MigrationsHistoryTable("__ef_migrations_history", "catalog"))
            .Options;
        return new CatalogDbContext(options);
    }
}
