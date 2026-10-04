using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Parrot.Catalog.Infrastructure.Data;

// Lets 'dotnet ef migrations add' build the writer context without a host; not used at runtime.
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("Catalog__ConnectionStrings__Migrator")
            ?? "Host=localhost;Port=5432;Database=parrot;Username=catalog_migrator;Password=catalog_migrator";
        DbContextOptions<CatalogDbContext> options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations_history", ModelConventions.Schema))
            .Options;
        return new CatalogDbContext(options);
    }
}
