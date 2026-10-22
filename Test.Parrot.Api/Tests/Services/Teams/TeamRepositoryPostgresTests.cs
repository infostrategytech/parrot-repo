using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Models;
using Parrot.Application.Teams;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.Teams;
using Test.Parrot.Api.Tests.Services.Integrations;

namespace Test.Parrot.Api.Tests.Services.Teams;

// The grants against a real PostgreSQL: one per workspace and one for all, each
// enforced by its own partial unique index, and replaced in one transaction.
// Run against a disposable database: see PARROT_TEST_POSTGRES.
public sealed class TeamRepositoryPostgresTests : IAsyncLifetime
{
    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private DbContextOptions<ApplicationDbContext>? _options;

    public async Task InitializeAsync()
    {
        string? server = Environment.GetEnvironmentVariable("PARROT_TEST_POSTGRES");
        if (server is null) return;
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(server) { Database = _database }.ConnectionString).Options;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureCreatedAsync();
        db.Users.AddRange(
            new ApplicationUser { Id = "owner", UserName = "owner@example.com", Email = "owner@example.com" },
            new ApplicationUser { Id = "ada", UserName = "ada@example.com", Email = "ada@example.com" });
        db.Workspaces.AddRange(new Workspace { Id = "ws-1", OwnerId = "owner" }, new Workspace { Id = "ws-2", OwnerId = "owner" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private static WorkspaceGrant Grant(string? workspaceId, params string[] channels) => new()
    {
        OwnerId = "owner",
        UserId = "ada",
        WorkspaceId = workspaceId,
        SystemRole = "conversation-manager",
        Channels = [.. channels],
    };

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task Grants_AreReplacedWhole_AndKeepTheirRoleAndChannels()
    {
        await using ApplicationDbContext db = new(_options!);
        TeamRepository repository = new(db);

        await repository.ReplaceGrantsAsync("owner", "ada", [Grant(null), Grant("ws-1", WorkspaceChannels.WhatsApp)]);
        await repository.ReplaceGrantsAsync("owner", "ada", [Grant("ws-2", WorkspaceChannels.Instagram)]);

        WorkspaceGrant only = Assert.Single(await repository.ListGrantsAsync("owner", "ada"));
        Assert.Equal("ws-2", only.WorkspaceId);
        Assert.Equal([WorkspaceChannels.Instagram], only.Channels);
        Assert.Equal("conversation-manager", only.SystemRole);
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task APerson_HoldsAtMostOneAllWorkspacesGrant_AndOnePerWorkspace()
    {
        await using ApplicationDbContext db = new(_options!);
        TeamRepository repository = new(db);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.ReplaceGrantsAsync("owner", "ada", [Grant(null), Grant(null)]));
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.ReplaceGrantsAsync("owner", "ada", [Grant("ws-1"), Grant("ws-1")]));
        db.ChangeTracker.Clear();

        // The failed replacements rolled back whole.
        Assert.Empty(await repository.ListGrantsAsync("owner", "ada"));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task AnInvitesGrants_RoundTripAsJson()
    {
        await using ApplicationDbContext db = new(_options!);
        TeamRepository repository = new(db);
        TeamInvite invite = new()
        {
            OwnerId = "owner", Email = "bob@example.com", NormalizedEmail = "BOB@EXAMPLE.COM", FullName = "Bob",
            TokenHash = TeamService.Hash("token"), InvitedByUserId = "owner", ExpiresAt = DateTime.UtcNow.AddDays(7),
            Grants = [new GrantSpec { WorkspaceId = null, SystemRole = "analyst", Channels = [WorkspaceChannels.TikTok] }],
        };
        await repository.AddInviteAsync(invite);

        await using ApplicationDbContext fresh = new(_options!);
        TeamInvite? found = await new TeamRepository(fresh).FindInviteByTokenHashAsync(TeamService.Hash("token"));

        Assert.NotNull(found);
        GrantSpec grant = Assert.Single(found.Grants);
        Assert.Null(grant.WorkspaceId);
        Assert.Equal([WorkspaceChannels.TikTok], grant.Channels);
        Assert.NotNull(await new TeamRepository(fresh).FindPendingInviteAsync("owner", "BOB@EXAMPLE.COM", DateTime.UtcNow));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task AnOwnersRoleNames_AreUniqueIgnoringCase_AndARoleInUseCannotBeDeleted()
    {
        await using ApplicationDbContext db = new(_options!);
        TeamRepository repository = new(db);
        OwnerRole role = new() { OwnerId = "owner", Name = "Analyst", NormalizedName = "ANALYST", Permissions = [WorkspacePermissions.AnalyticsDashboard] };

        Assert.True(await repository.TryAddRoleAsync(role));
        Assert.False(await repository.TryAddRoleAsync(new OwnerRole { OwnerId = "owner", Name = "analyst", NormalizedName = "ANALYST" }));

        await repository.ReplaceGrantsAsync("owner", "ada", [new WorkspaceGrant { OwnerId = "owner", UserId = "ada", WorkspaceId = "ws-1", CustomRoleId = role.Id }]);
        Assert.Equal(["ada"], await repository.ListRoleHoldersAsync("owner", role.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.DeleteRoleAsync(role));
    }

    [ConnectionMessageReceiptPostgresTests.PostgresFact]
    public async Task AGrant_NamesExactlyOneRole()
    {
        await using ApplicationDbContext db = new(_options!);
        TeamRepository repository = new(db);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.ReplaceGrantsAsync("owner", "ada",
            [new WorkspaceGrant { OwnerId = "owner", UserId = "ada", WorkspaceId = "ws-1" }]));
    }
}
