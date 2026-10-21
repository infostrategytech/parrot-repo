using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.Messaging;

namespace Test.Parrot.Api.Tests.Services.Integrations;

// The message worker records each stored Facebook message against its Page connection in
// PostgreSQL. Run against a disposable database: PARROT_TEST_POSTGRES is a connection string
// whose user may create databases, e.g. Host=localhost;Port=15432;Username=postgres;Password=…
public class ConnectionMessageReceiptPostgresTests : IAsyncLifetime
{
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARROT_TEST_POSTGRES") is null)
                Skip = "Set PARROT_TEST_POSTGRES to a disposable local PostgreSQL instance.";
        }
    }

    private sealed class Contexts(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private const string PageId = "185860297939218";
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
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<FacebookConnection> PageAsync()
    {
        await using ApplicationDbContext db = new(_options!);
        ApplicationUser user = new() { UserName = "owner@example.com", Email = "owner@example.com" };
        db.Users.Add(user);
        // A tenant's first workspace takes the user's id, as sign-in makes it.
        db.Workspaces.Add(new Workspace { Id = user.Id, OwnerId = user.Id });
        FacebookConnection page = new()
        {
            WorkspaceId = user.Id, VerifyTokenEncrypted = "encrypted", PageId = PageId, PageName = "Oma's Kitchen",
            WebhookVerifiedAt = DateTime.UtcNow,
        };
        db.FacebookConnections.Add(page);
        await db.SaveChangesAsync();
        return page;
    }

    private static IncomingMessage Message(FacebookConnection page, string channelId = PageId) => new()
    {
        ConnectionId = page.Id, OwnerUserId = page.WorkspaceId, MessageId = "facebook:m_1",
        Platform = MessagingPlatform.Facebook, ExternalAccountId = channelId, From = "6215837091842310",
        Timestamp = "1791447800000", Type = "text", ChannelId = channelId, ChannelDisplayName = "",
        ReceivedAt = DateTime.UtcNow,
    };

    [PostgresFact]
    public async Task AMessageForAnActivePage_IsRecorded()
    {
        FacebookConnection page = await PageAsync();

        await new ConnectionMessageReceipt(new Contexts(_options!)).RecordAsync(Message(page), CancellationToken.None);
    }

    // Disconnected after the message was queued: stored, nothing to record, and the consumer moves on.
    [PostgresFact]
    public async Task AMessageForAPageDisconnectedMeanwhile_DoesNotHoldUpTheWorker()
    {
        FacebookConnection page = await PageAsync();
        await using (ApplicationDbContext db = new(_options!))
            await db.FacebookConnections.Where(c => c.Id == page.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DisabledAt, DateTime.UtcNow));

        await new ConnectionMessageReceipt(new Contexts(_options!)).RecordAsync(Message(page), CancellationToken.None);
    }

    // A message for another Page under this connection is a real fault: retried, then dead-lettered.
    [PostgresFact]
    public async Task AMessageForAnotherPage_IsAFault()
    {
        FacebookConnection page = await PageAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ConnectionMessageReceipt(new Contexts(_options!)).RecordAsync(Message(page, "999999999"), CancellationToken.None));
    }

    private async Task<InstagramConnection> AccountAsync()
    {
        await using ApplicationDbContext db = new(_options!);
        ApplicationUser user = new() { UserName = "ig@example.com", Email = "ig@example.com" };
        db.Users.Add(user);
        // A tenant's first workspace takes the user's id, as sign-in makes it.
        db.Workspaces.Add(new Workspace { Id = user.Id, OwnerId = user.Id });
        InstagramConnection account = new()
        {
            WorkspaceId = user.Id, VerifyTokenEncrypted = "encrypted", AccountId = "17841400000000000", Username = "omaskitchen",
            WebhookVerifiedAt = DateTime.UtcNow,
        };
        db.InstagramConnections.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static IncomingMessage InstagramMessage(InstagramConnection account, string channelId = "17841400000000000") => new()
    {
        ConnectionId = account.Id, OwnerUserId = account.WorkspaceId, MessageId = "instagram:ig_1",
        Platform = MessagingPlatform.Instagram, ExternalAccountId = channelId, From = "5834920000000000",
        Timestamp = "1791447800000", Type = "text", ChannelId = channelId, ChannelDisplayName = "",
        ReceivedAt = DateTime.UtcNow,
    };

    [PostgresFact]
    public async Task Instagram_AMessageForAnActiveAccount_IsRecorded_AndADisconnectedOneDoesNotHoldUpTheWorker()
    {
        InstagramConnection account = await AccountAsync();
        ConnectionMessageReceipt receipts = new(new Contexts(_options!));

        await receipts.RecordAsync(InstagramMessage(account), CancellationToken.None);
        await using (ApplicationDbContext db = new(_options!))
            await db.InstagramConnections.Where(c => c.Id == account.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DisabledAt, DateTime.UtcNow));
        await receipts.RecordAsync(InstagramMessage(account), CancellationToken.None);
    }

    [PostgresFact]
    public async Task Instagram_AMessageForAnotherAccount_IsAFault()
    {
        InstagramConnection account = await AccountAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ConnectionMessageReceipt(new Contexts(_options!)).RecordAsync(InstagramMessage(account, "999"), CancellationToken.None));
    }
}
