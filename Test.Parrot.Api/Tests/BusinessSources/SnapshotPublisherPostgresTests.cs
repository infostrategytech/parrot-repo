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
using Parrot.Infrastructure.BusinessSources;
using Parrot.Infrastructure.Data;
using static Test.Parrot.Api.Tests.Services.Integrations.ConnectionMessageReceiptPostgresTests;

namespace Test.Parrot.Api.Tests.BusinessSources;

// What the API queues for business.knowledge.sources, read back from a real PostgreSQL outbox.
// PARROT_TEST_POSTGRES: a connection string whose user may create databases.
public sealed class SnapshotPublisherPostgresTests : IAsyncLifetime
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
        db.Workspaces.Add(new Workspace { Id = "ws-a", OwnerId = "owner", Name = "Adire House" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    [PostgresFact]
    public async Task PublishAsync_QueuesSchemaTwoWithTheOwnerAsTenantAndTheTrigger()
    {
        await using (ApplicationDbContext seed = new(_options!))
        {
            WhatsAppConnection connection = new()
            {
                WorkspaceId = "ws-a",
                Name = "Test connection",
                WhatsAppBusinessAccountId = "waba-1",
                VerifyTokenEncrypted = "enc:verify",
            };
            seed.WhatsAppConnections.Add(connection);
            seed.BusinessSources.Add(new BusinessSource
            {
                WorkspaceId = "ws-a",
                AuthorizationConnectionId = connection.Id,
                Channel = Channel.WhatsApp,
                Kind = BusinessSourceKind.Profile,
                ExternalId = "15550001234",
                DisplayName = "Adire House",
                IsActive = true,
                MetadataSyncStatus = MetadataSyncStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using (ApplicationDbContext db = new(_options!))
        {
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
            SnapshotPublisher publisher = new(db, Options.Create(new KafkaSettings()),
                new AppLogger<SnapshotPublisher>(NullLogger<SnapshotPublisher>.Instance));
            await publisher.PublishAsync("ws-a", SnapshotTrigger.ChannelConnected);
            await transaction.CommitAsync();
        }

        await using ApplicationDbContext read = new(_options!);
        OutboxEvent queued = await read.OutboxEvents.SingleAsync();
        using JsonDocument payload = JsonDocument.Parse(queued.Payload);
        JsonElement root = payload.RootElement;
        JsonElement data = root.GetProperty("data");

        // The outbox row's id is the event id, so a retried row is the same event to Omni.
        Assert.Equal(queued.Id.ToString(), root.GetProperty("eventId").GetString());
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("correlationId").GetString()));
        Assert.Equal("ws-a", data.GetProperty("workspaceId").GetString());
        Assert.Equal("owner", data.GetProperty("tenantId").GetString());
        Assert.Equal("ChannelConnected", data.GetProperty("trigger").GetString());
        Assert.Equal(1, data.GetProperty("snapshotVersion").GetInt64());
        Assert.Equal("Adire House", data.GetProperty("business").GetProperty("name").GetString());
        JsonElement source = Assert.Single(data.GetProperty("sources").EnumerateArray());
        Assert.Equal("whatsapp:15550001234", source.GetProperty("sourceId").GetString());
        Assert.Equal("waba-1", source.GetProperty("providerData").GetProperty("wabaId").GetString());
        Assert.Equal("15550001234", source.GetProperty("providerData").GetProperty("phoneNumberId").GetString());
        Assert.Empty(NamesWithUnderscores(root));
    }

    private static IEnumerable<string> NamesWithUnderscores(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .SelectMany(p => (p.Name.Contains('_') ? [p.Name] : Array.Empty<string>()).Concat(NamesWithUnderscores(p.Value))),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(NamesWithUnderscores),
        _ => [],
    };
}
