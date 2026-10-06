using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Catalog.Infrastructure.Services;

namespace Parrot.Catalog.Tools.Commands;

internal static class MigrateTypeVersion
{
    public static async Task<int> RunAsync(IConfiguration configuration, Dictionary<string, string?> args)
    {
        Guid tenantId = Args.RequireGuid(args, "tenant");
        string typeKey = Args.Require(args, "type");
        int from = Args.Int(args, "from", 0);
        int to = Args.Int(args, "to", 0);
        bool dryRun = Args.Flag(args, "dry-run");
        if (from < 1 || to < 1)
        {
            throw new ArgumentException("--from and --to must be version numbers >= 1.");
        }

        await using ServiceProvider provider = Hosting.Services(configuration);
        using IServiceScope scope = Hosting.TenantScope(provider, tenantId);
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        ProductType type = await db.ProductTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Key == typeKey)
            ?? throw new InvalidOperationException($"Product type '{typeKey}' was not found for tenant {tenantId}.");

        ITypeVersionMigrator migrator = scope.ServiceProvider.GetRequiredService<ITypeVersionMigrator>();
        TypeVersionMigrationReport report = await migrator.MigrateAsync(type.Id, from, to, dryRun, CancellationToken.None);

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(Json.Wire) { WriteIndented = true }));
        Console.WriteLine(dryRun
            ? $"Dry run: {report.Total} product(s) checked, {report.Failed} would fail. Nothing was changed."
            : $"Migrated {report.Migrated} of {report.Total} product(s); {report.Failed} failed and were left on version {from}.");
        return report.Failed == 0 ? 0 : 3;
    }
}
