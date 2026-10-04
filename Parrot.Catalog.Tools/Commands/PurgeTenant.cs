using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Parrot.Catalog.Infrastructure.Data;

namespace Parrot.Catalog.Tools.Commands;

/// <summary>
/// Tenant offboarding. Runs as the migrator inside one transaction with the tenant set,
/// so RLS still scopes every DELETE, and with app.allow_purge on so the append-only
/// trigger lets product_versions and audit_log go.
/// </summary>
internal static class PurgeTenant
{
    public static async Task<int> RunAsync(IConfiguration configuration, Dictionary<string, string?> args)
    {
        Guid tenantId = Args.RequireGuid(args, "tenant");
        Guid confirm = Args.RequireGuid(args, "confirm");
        if (tenantId != confirm)
        {
            throw new ArgumentException("--confirm must repeat the tenant id.");
        }

        bool keepAudit = Args.Flag(args, "keep-audit");
        await using CatalogDbContext db = Hosting.Migrator(configuration);
        await db.Database.OpenConnectionAsync();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync();

        await db.Database.ExecuteSqlRawAsync("SELECT set_config('app.tenant_id', {0}, true)", tenantId.ToString("D"));
        await db.Database.ExecuteSqlRawAsync("SELECT set_config('app.allow_purge', 'on', true)");

        int total = 0;
        // Products first: their cascade takes variants, index rows, offers, prices, availability,
        // relationships, media, versions and documents. Types can then go (products restrict
        // them), and the tenant row cascades catalogs, categories and the closure table.
        total += await db.Database.ExecuteSqlRawAsync("DELETE FROM catalog.products WHERE tenant_id = {0}", tenantId);
        total += await db.Database.ExecuteSqlRawAsync("DELETE FROM catalog.product_types WHERE tenant_id = {0}", tenantId);
        total += await db.Database.ExecuteSqlRawAsync("DELETE FROM catalog.tenants WHERE id = {0}", tenantId);
        total += await db.Database.ExecuteSqlRawAsync("DELETE FROM catalog.outbox WHERE tenant_id = {0}", tenantId);
        total += await db.Database.ExecuteSqlRawAsync("DELETE FROM catalog.consumer_inbox WHERE tenant_id = {0}", tenantId);
        if (!keepAudit)
        {
            total += await db.Database.ExecuteSqlRawAsync("DELETE FROM catalog.audit_log WHERE tenant_id = {0}", tenantId);
        }

        await tx.CommitAsync();
        Console.WriteLine($"Purged tenant {tenantId}: {total} top-level row(s) deleted (cascades not counted). Audit log {(keepAudit ? "kept" : "deleted")}.");
        return 0;
    }
}
