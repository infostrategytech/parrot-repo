using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.BusinessSources;
using Parrot.Infrastructure.Data;
using static Test.Parrot.Api.Tests.Services.Integrations.ConnectionMessageReceiptPostgresTests;

namespace Test.Parrot.Api.Tests.BusinessSources;

// Which trigger each channel action puts on the snapshot, against a real PostgreSQL database.
// PARROT_TEST_POSTGRES: a connection string whose user may create databases.
public sealed class ConnectionSourceSyncServicePostgresTests : IAsyncLifetime
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
        db.Workspaces.AddRange(
            new Workspace { Id = "ws-a", OwnerId = "owner", Name = "Adire House" },
            new Workspace { Id = "ws-b", OwnerId = "owner", Name = "Adire Lagos" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private static ConnectionSourceSyncService Service(ApplicationDbContext db) =>
        new(db,
            new BusinessSourceRepository(db),
            new SnapshotPublisher(db, Options.Create(new KafkaSettings()), new AppLogger<SnapshotPublisher>(NullLogger<SnapshotPublisher>.Instance)),
            new AppLogger<ConnectionSourceSyncService>(NullLogger<ConnectionSourceSyncService>.Instance));

    private async Task<JsonElement> QueuedDataAsync()
    {
        await using ApplicationDbContext db = new(_options!);
        OutboxEvent queued = await db.OutboxEvents.SingleAsync();
        using JsonDocument payload = JsonDocument.Parse(queued.Payload);
        return payload.RootElement.GetProperty("data").Clone();
    }

    private async Task<BusinessSource> SourceAsync(Guid connectionId)
    {
        await using ApplicationDbContext db = new(_options!);
        return await db.BusinessSources.SingleAsync(s => s.AuthorizationConnectionId == connectionId);
    }

    private async Task<Guid> SeedCompletedWhatsAppSourceAsync(string workspaceId)
    {
        Guid connectionId = Guid.NewGuid();
        await using ApplicationDbContext db = new(_options!);
        db.BusinessSources.Add(new BusinessSource
        {
            WorkspaceId = workspaceId,
            AuthorizationConnectionId = connectionId,
            Channel = Channel.WhatsApp,
            Kind = BusinessSourceKind.Profile,
            ExternalId = "15550001234",
            IsActive = true,
            MetadataSyncStatus = MetadataSyncStatus.Completed,
            MetadataSyncAttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return connectionId;
    }

    [PostgresFact]
    public async Task SyncWhatsAppProfileAsync_QueuesChannelConnected_AndLeavesItForTheWorkersSnapshot()
    {
        Guid connectionId = Guid.NewGuid();

        await using (ApplicationDbContext db = new(_options!))
        {
            await Service(db).SyncWhatsAppProfileAsync("ws-a", connectionId, "15550001234");
        }

        Assert.Equal("ChannelConnected", (await QueuedDataAsync()).GetProperty("trigger").GetString());
        Assert.Equal(SnapshotTrigger.ChannelConnected, (await SourceAsync(connectionId)).MetadataSyncTrigger);
    }

    [PostgresFact]
    public async Task RequestMetadataRefreshAsync_LeavesManualRefreshForTheWorkersSnapshot()
    {
        Guid connectionId = await SeedCompletedWhatsAppSourceAsync("ws-a");

        await using (ApplicationDbContext db = new(_options!))
        {
            await Service(db).RequestMetadataRefreshAsync("ws-a", connectionId);
        }

        BusinessSource source = await SourceAsync(connectionId);
        Assert.Equal(SnapshotTrigger.ManualRefresh, source.MetadataSyncTrigger);
        Assert.Equal(MetadataSyncStatus.Pending, source.MetadataSyncStatus);
        // Nothing is queued yet: the worker sends the snapshot once it has read the channel again.
        await using ApplicationDbContext read = new(_options!);
        Assert.Empty(await read.OutboxEvents.ToListAsync());
    }

    [PostgresFact]
    public async Task RequestMetadataRefreshAsync_WithAnotherWorkspacesConnection_IsTheSameNotFoundAsARandomId_AndChangesNothing()
    {
        Guid theirs = await SeedCompletedWhatsAppSourceAsync("ws-b");

        await using ApplicationDbContext db = new(_options!);
        NotFoundException foreign = await Assert.ThrowsAsync<NotFoundException>(() => Service(db).RequestMetadataRefreshAsync("ws-a", theirs));
        NotFoundException random = await Assert.ThrowsAsync<NotFoundException>(() => Service(db).RequestMetadataRefreshAsync("ws-a", Guid.NewGuid()));

        Assert.Equal(random.Message, foreign.Message);
        BusinessSource untouched = await SourceAsync(theirs);
        Assert.Null(untouched.MetadataSyncTrigger);
        Assert.Equal(MetadataSyncStatus.Completed, untouched.MetadataSyncStatus);
    }

    [PostgresFact]
    public async Task SyncWhatsAppCatalogAsync_QueuesManualRefresh()
    {
        await using (ApplicationDbContext db = new(_options!))
        {
            await Service(db).SyncWhatsAppCatalogAsync("ws-a", Guid.NewGuid(), "catalog-1");
        }

        Assert.Equal("ManualRefresh", (await QueuedDataAsync()).GetProperty("trigger").GetString());
    }

    [PostgresFact]
    public async Task DeactivateConnectionSourcesAsync_QueuesChannelDisconnected_WithoutTheSource()
    {
        Guid connectionId = await SeedCompletedWhatsAppSourceAsync("ws-a");

        await using (ApplicationDbContext db = new(_options!))
        {
            await Service(db).DeactivateConnectionSourcesAsync("ws-a", connectionId);
        }

        JsonElement data = await QueuedDataAsync();
        Assert.Equal("ChannelDisconnected", data.GetProperty("trigger").GetString());
        Assert.Empty(data.GetProperty("sources").EnumerateArray());
    }

    [PostgresFact]
    public async Task PublishWorkspaceCreatedAsync_QueuesTheOwnerLinkWithTheNameAndNoSources()
    {
        await SeedCompletedWhatsAppSourceAsync("ws-a");

        await using (ApplicationDbContext db = new(_options!))
        {
            await Service(db).PublishWorkspaceCreatedAsync("ws-b");
        }

        JsonElement data = await QueuedDataAsync();
        Assert.Equal("ws-b", data.GetProperty("workspaceId").GetString());
        Assert.Equal("owner", data.GetProperty("tenantId").GetString());
        Assert.Equal("WorkspaceCreated", data.GetProperty("trigger").GetString());
        Assert.Equal("Adire Lagos", data.GetProperty("business").GetProperty("name").GetString());
        // ws-a's channel stays ws-a's: the new workspace starts with none of its own.
        Assert.Empty(data.GetProperty("sources").EnumerateArray());
    }
}
