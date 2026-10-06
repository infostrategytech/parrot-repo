using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Parrot.Catalog.Infrastructure.Data;

namespace Parrot.Catalog.Tools.Commands;

internal static class MigrateDb
{
    public static async Task<int> RunAsync(IConfiguration configuration)
    {
        await using CatalogDbContext db = Hosting.Migrator(configuration);
        IEnumerable<string> pending = await db.Database.GetPendingMigrationsAsync();
        List<string> list = pending.ToList();
        if (list.Count == 0)
        {
            Console.WriteLine("No pending catalog migrations.");
            return 0;
        }

        Console.WriteLine($"Applying {list.Count} migration(s): {string.Join(", ", list)}");
        await db.Database.MigrateAsync();
        Console.WriteLine("Done.");
        return 0;
    }
}
