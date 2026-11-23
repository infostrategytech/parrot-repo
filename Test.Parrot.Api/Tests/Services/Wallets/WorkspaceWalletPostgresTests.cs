using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.Wallets;
using Parrot.Infrastructure.Workspaces;
using Test.Parrot.Api.Tests.Services.Integrations;

namespace Test.Parrot.Api.Tests.Services.Wallets;

// Wallets against a real PostgreSQL: one made with every workspace, however it's made, and only ever one.
// Run against a disposable database: see PARROT_TEST_POSTGRES.
public sealed class WorkspaceWalletPostgresTests : IAsyncLifetime
{
    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private DbContextOptions<ApplicationDbContext>? _options;

    public async Task InitializeAsync()
    {
        string? server = Environment.GetEnvironmentVariable("PARROT_TEST_POSTGRES");
        if (server is null) return;
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(server) { Database = _database }.ConnectionString).Options;
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private async Task CreatedAsync()
    {
        await using ApplicationDbContext db = new(_options!);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner@example.com", Email = "owner@example.com" });
        await db.SaveChangesAsync();
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task A_new_workspace_is_saved_with_an_empty_naira_wallet()
    {
        await CreatedAsync();
        await using (ApplicationDbContext db = new(_options!))
        {
            Assert.True(await new WorkspaceRepository(db).TryAddAsync(new Workspace { Id = "ws-1", OwnerId = "owner", Name = "Mama Put" }));
        }

        await using ApplicationDbContext check = new(_options!);
        WorkspaceWallet wallet = await check.WorkspaceWallets.SingleAsync();
        Assert.Equal(("ws-1", "NGN", 0L), (wallet.WorkspaceId, wallet.Currency, wallet.BalanceMinor));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task Two_sign_ins_racing_to_make_the_first_workspace_leave_one_workspace_and_one_wallet()
    {
        await CreatedAsync();

        bool[] made = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using ApplicationDbContext db = new(_options!);
            return await new WorkspaceRepository(db).TryAddAsync(new Workspace { Id = "first", OwnerId = "owner" });
        }));

        Assert.Single(made, m => m);
        await using ApplicationDbContext check = new(_options!);
        Assert.Equal(1, await check.Workspaces.CountAsync());
        Assert.Equal("first", (await check.WorkspaceWallets.SingleAsync()).WorkspaceId);
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task A_workspace_without_a_wallet_gets_one_when_asked_and_only_one_however_many_ask()
    {
        await CreatedAsync();
        await using (ApplicationDbContext db = new(_options!))
        {
            // Saved the old way, with no wallet, as workspaces made before wallets were.
            db.Workspaces.Add(new Workspace { Id = "old", OwnerId = "owner" });
            await db.SaveChangesAsync();
        }

        WorkspaceWallet?[] wallets = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using ApplicationDbContext db = new(_options!);
            return await new EfWalletRepository(db).GetOrCreateAsync("old", default);
        }));

        Assert.All(wallets, w => Assert.Equal(("old", 0L), (w!.WorkspaceId, w.BalanceMinor)));
        await using ApplicationDbContext check = new(_options!);
        Assert.Equal(1, await check.WorkspaceWallets.CountAsync());
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task No_wallet_is_made_for_a_workspace_that_doesnt_exist()
    {
        await CreatedAsync();
        await using ApplicationDbContext db = new(_options!);

        Assert.Null(await new EfWalletRepository(db).GetOrCreateAsync("nowhere", default));
        Assert.Equal(0, await db.WorkspaceWallets.CountAsync());
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task The_database_refuses_a_balance_below_zero()
    {
        await CreatedAsync();
        await using ApplicationDbContext db = new(_options!);
        await new WorkspaceRepository(db).TryAddAsync(new Workspace { Id = "ws-1", OwnerId = "owner" });

        PostgresException refused = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("""UPDATE "WorkspaceWallets" SET "BalanceMinor" = -1 WHERE "WorkspaceId" = 'ws-1'"""));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task The_migration_gives_every_existing_workspace_a_wallet()
    {
        await using ApplicationDbContext db = new(_options!);
        IMigrator migrator = db.GetService<IMigrator>();
        string[] all = db.Database.GetMigrations().ToArray();
        string wallets = all.Single(m => m.EndsWith("_AddWorkspaceWallets", StringComparison.Ordinal));
        await migrator.MigrateAsync(all[Array.IndexOf(all, wallets) - 1]);

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "AspNetUsers" ("Id", "UserName", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('owner', 'owner@example.com', true, false, false, false, 0);
            INSERT INTO "Workspaces" ("Id", "OwnerId") VALUES ('a', 'owner'), ('b', 'owner');
            """);
        await migrator.MigrateAsync(wallets);

        Assert.Equal(["a", "b"], await db.WorkspaceWallets.Select(w => w.WorkspaceId).OrderBy(id => id).ToListAsync());
    }
}
