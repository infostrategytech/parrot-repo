using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Parrot.Catalog.Infrastructure.Events;

namespace Parrot.Catalog.Tools.Commands;

internal static class SweepOutbox
{
    public static async Task<int> RunAsync(IConfiguration configuration, Dictionary<string, string?> args)
    {
        int days = Args.Int(args, "days", 7);
        string connection = configuration["Catalog:ConnectionStrings:Relay"] ?? configuration["Catalog:ConnectionStrings:Migrator"]
            ?? throw new InvalidOperationException("Catalog:ConnectionStrings:Relay must be configured.");
        DbContextOptions<RelayDbContext> options = new DbContextOptionsBuilder<RelayDbContext>().UseNpgsql(connection).Options;
        await using RelayDbContext db = new RelayDbContext(options);
        DateTime cutoff = DateTime.UtcNow.AddDays(-days);
        int deleted = await db.Outbox.Where(o => o.PublishedAt != null && o.PublishedAt < cutoff).ExecuteDeleteAsync();
        Console.WriteLine($"Deleted {deleted} published outbox row(s) older than {days} day(s).");
        return 0;
    }
}
