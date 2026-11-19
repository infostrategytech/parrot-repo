using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.BusinessSources;
using Parrot.Infrastructure.Data;
using static Test.Parrot.Api.Tests.Services.Integrations.ConnectionMessageReceiptPostgresTests;

namespace Test.Parrot.Api.Tests.BusinessSources;

// Core upsert and deactivation behaviour against a real PostgreSQL database.
// Run against a disposable database: set PARROT_TEST_POSTGRES to a connection string
// whose user may create databases, e.g. Host=localhost;Port=15432;Username=postgres;Password=…
public sealed class BusinessSourceRepositoryPostgresTests : IAsyncLifetime
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
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner@test.com", Email = "owner@test.com" });
        db.Workspaces.Add(new Workspace { Id = "ws-1", OwnerId = "owner" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private static BusinessSource WaProfile(string workspaceId, Guid connectionId, string phoneNumberId) => new()
    {
        WorkspaceId = workspaceId,
        AuthorizationConnectionId = connectionId,
        Channel = Channel.WhatsApp,
        Kind = BusinessSourceKind.Profile,
        ExternalId = phoneNumberId,
        IsActive = true,
        MetadataSyncStatus = MetadataSyncStatus.Pending,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [PostgresFact]
    public async Task UpsertAsync_WhenCalledTwiceWithSameKey_UpdatesRowNotDuplicate()
    {
        Guid connectionId = Guid.NewGuid();

        // First connection.
        await using (ApplicationDbContext db1 = new(_options!))
        {
            BusinessSource first = WaProfile("ws-1", connectionId, "15550000001");
            await new BusinessSourceRepository(db1).UpsertAsync(first);
            await db1.SaveChangesAsync();
        }

        // Reconnect: same phone number, new connection id.
        Guid newConnectionId = Guid.NewGuid();
        Guid updatedId;
        await using (ApplicationDbContext db2 = new(_options!))
        {
            BusinessSource second = WaProfile("ws-1", newConnectionId, "15550000001");
            BusinessSource result = await new BusinessSourceRepository(db2).UpsertAsync(second);
            await db2.SaveChangesAsync();
            updatedId = result.Id;
            Assert.Equal(newConnectionId, result.AuthorizationConnectionId);
        }

        // Exactly one row in the DB.
        await using ApplicationDbContext db3 = new(_options!);
        List<BusinessSource> all = await db3.BusinessSources.Where(s => s.WorkspaceId == "ws-1").ToListAsync();
        Assert.Single(all);
        Assert.Equal(updatedId, all[0].Id);
    }

    [PostgresFact]
    public async Task UpsertAsync_WhenSourceWasDeactivated_ReactivatesAndResetsAttemptCount()
    {
        Guid connectionId = Guid.NewGuid();

        // Seed an inactive, failed source.
        Guid seededId;
        await using (ApplicationDbContext db1 = new(_options!))
        {
            BusinessSource seed = WaProfile("ws-1", connectionId, "15550000002");
            seed.IsActive = false;
            seed.MetadataSyncStatus = MetadataSyncStatus.Failed;
            seed.MetadataSyncAttemptCount = 5;
            db1.BusinessSources.Add(seed);
            await db1.SaveChangesAsync();
            seededId = seed.Id;
        }

        // Reconnect.
        await using (ApplicationDbContext db2 = new(_options!))
        {
            BusinessSource reconnect = WaProfile("ws-1", connectionId, "15550000002");
            BusinessSource result = await new BusinessSourceRepository(db2).UpsertAsync(reconnect);
            await db2.SaveChangesAsync();

            Assert.Equal(seededId, result.Id);
            Assert.True(result.IsActive);
            Assert.Equal(MetadataSyncStatus.Pending, result.MetadataSyncStatus);
            Assert.Equal(0, result.MetadataSyncAttemptCount);
        }
    }

    [PostgresFact]
    public async Task DeactivateByConnectionAsync_MarksAllSourcesForConnectionInactive()
    {
        Guid connectionId = Guid.NewGuid();

        await using (ApplicationDbContext db1 = new(_options!))
        {
            db1.BusinessSources.Add(WaProfile("ws-1", connectionId, "15550000003"));
            db1.BusinessSources.Add(new BusinessSource
            {
                WorkspaceId = "ws-1",
                AuthorizationConnectionId = connectionId,
                Channel = Channel.WhatsApp,
                Kind = BusinessSourceKind.Catalog,
                ExternalId = "cat-001",
                IsActive = true,
                MetadataSyncStatus = MetadataSyncStatus.Unavailable,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            // A different connection's source must not be touched.
            db1.BusinessSources.Add(WaProfile("ws-1", Guid.NewGuid(), "15550000099"));
            await db1.SaveChangesAsync();
        }

        await using (ApplicationDbContext db2 = new(_options!))
        {
            await new BusinessSourceRepository(db2).DeactivateByConnectionAsync("ws-1", connectionId, DateTime.UtcNow);
            await db2.SaveChangesAsync();
        }

        await using ApplicationDbContext db3 = new(_options!);
        List<BusinessSource> all = await db3.BusinessSources.Where(s => s.WorkspaceId == "ws-1").ToListAsync();
        Assert.Equal(2, all.Count(s => s.AuthorizationConnectionId == connectionId && !s.IsActive));
        Assert.True(all.Single(s => s.ExternalId == "15550000099").IsActive);
    }

    [PostgresFact]
    // A worker stopped mid-read (a deploy, a crash) leaves the source processing; once its lease
    // runs out another worker must take it, or it waits forever.
    public async Task ClaimBatchForMetadataAsync_WhenProcessingLeaseHasRunOut_ClaimsItAgain()
    {
        await using (ApplicationDbContext seed = new(_options!))
        {
            BusinessSource abandoned = WaProfile("ws-1", Guid.NewGuid(), "15550000010");
            abandoned.MetadataSyncStatus = MetadataSyncStatus.Processing;
            abandoned.MetadataSyncLockedUntil = DateTime.UtcNow.AddMinutes(-1);
            BusinessSource held = WaProfile("ws-1", Guid.NewGuid(), "15550000011");
            held.MetadataSyncStatus = MetadataSyncStatus.Processing;
            held.MetadataSyncLockedUntil = DateTime.UtcNow.AddMinutes(5);
            seed.BusinessSources.AddRange(abandoned, held);
            await seed.SaveChangesAsync();
        }

        await using ApplicationDbContext db = new(_options!);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
        IReadOnlyList<BusinessSource> claimed = await new BusinessSourceRepository(db)
            .ClaimBatchForMetadataAsync(10, TimeSpan.FromMinutes(5), kind: BusinessSourceKind.Profile);

        BusinessSource only = Assert.Single(claimed);
        Assert.Equal("15550000010", only.ExternalId);
        Assert.True(only.MetadataSyncLockedUntil > DateTime.UtcNow);
    }
}
