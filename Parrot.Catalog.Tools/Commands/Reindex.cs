using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Parrot.Catalog.Application.Registry;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Catalog.Infrastructure.Services;

namespace Parrot.Catalog.Tools.Commands;

/// <summary>Rebuilds derived data from the JSONB truth. Safe to run any time; the index is never truth.</summary>
internal static class Reindex
{
    public static async Task<int> RunAsync(IConfiguration configuration, Dictionary<string, string?> args)
    {
        Guid tenantId = Args.RequireGuid(args, "tenant");
        await using ServiceProvider provider = Hosting.Services(configuration);
        using IServiceScope scope = Hosting.TenantScope(provider, tenantId);
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        IWriteSchemaRegistry registry = scope.ServiceProvider.GetRequiredService<IWriteSchemaRegistry>();
        DerivedDataRebuilder rebuilder = scope.ServiceProvider.GetRequiredService<DerivedDataRebuilder>();

        List<Product> products = await db.Products.Where(p => p.DeletedAt == null).ToListAsync();
        int count = 0;
        foreach (Product product in products)
        {
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync();
            TypeSchema schema = await registry.GetAsync(product.ProductTypeId, product.TypeVersion, CancellationToken.None);
            await rebuilder.RebuildAsync(product, schema, CancellationToken.None);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            count++;
        }

        Console.WriteLine($"Rebuilt derived data for {count} product(s) of tenant {tenantId}.");
        return 0;
    }
}
