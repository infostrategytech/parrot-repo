using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.SocialMediaIntegrations;
using static Test.Parrot.Api.Tests.Services.Integrations.ConnectionMessageReceiptPostgresTests;

namespace Test.Parrot.Api.Tests.Services.Integrations;

// The renewal queries against real PostgreSQL: which tokens are due, and that a renewal never
// overwrites a token the owner pasted while it was running.
public class InstagramTokenRepositoryPostgresTests : IAsyncLifetime
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
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<InstagramConnection> AccountAsync(string name, DateTime renewedAt, string token = "enc:token",
        DateTime? rejectedAt = null, DateTime? disabledAt = null)
    {
        await using ApplicationDbContext db = new(_options!);
        ApplicationUser user = new() { UserName = $"{name}@example.com", Email = $"{name}@example.com" };
        db.Users.Add(user);
        InstagramConnection account = new()
        {
            OwnerUserId = user.Id, VerifyTokenEncrypted = "encrypted", AccountId = $"1784140000000{Random.Shared.Next(1000, 9999)}",
            Username = name, WebhookVerifiedAt = DateTime.UtcNow, AccessTokenEncrypted = token,
            AccessTokenRenewedAt = renewedAt, AccessTokenExpiresAt = renewedAt.AddDays(60),
            AccessTokenRejectedAt = rejectedAt, DisabledAt = disabledAt,
        };
        db.InstagramConnections.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    [PostgresFact]
    public async Task OnlyActiveUnrefusedTokensPastTheCutoff_AreDue_OldestFirst()
    {
        DateTime now = DateTime.UtcNow;
        InstagramConnection oldest = await AccountAsync("oldest", now.AddDays(-45));
        InstagramConnection due = await AccountAsync("due", now.AddDays(-31));
        await AccountAsync("fresh", now.AddDays(-5));
        await AccountAsync("refused", now.AddDays(-40), rejectedAt: now.AddDays(-1));
        await AccountAsync("disconnected", now.AddDays(-40), disabledAt: now.AddDays(-1));

        await using ApplicationDbContext db = new(_options!);
        IReadOnlyList<InstagramConnection> found = await new InstagramConnectionRepository(db).GetDueForTokenRenewalAsync(now.AddDays(-30), 50);

        Assert.Equal([oldest.Id, due.Id], found.Select(c => c.Id));
    }

    [PostgresFact]
    public async Task ARenewal_NeverOverwritesATokenPastedMeanwhile()
    {
        DateTime now = DateTime.UtcNow;
        InstagramConnection account = await AccountAsync("pasted", now.AddDays(-31), token: "enc:new-from-owner");

        await using ApplicationDbContext db = new(_options!);
        InstagramConnectionRepository repository = new(db);
        Assert.False(await repository.SaveRenewedTokenAsync(account.Id, "enc:old", "enc:renewed", now, now.AddDays(60)));
        await repository.MarkTokenRejectedAsync(account.Id, "enc:old", now);

        InstagramConnection stored = await db.InstagramConnections.AsNoTracking().SingleAsync(c => c.Id == account.Id);
        Assert.Equal("enc:new-from-owner", stored.AccessTokenEncrypted);
        Assert.Null(stored.AccessTokenRejectedAt);
    }

    [PostgresFact]
    public async Task ARenewal_ReplacesTheTokenItRenewed_AndPastingANewOneClearsARefusal()
    {
        DateTime now = DateTime.UtcNow;
        InstagramConnection account = await AccountAsync("renewed", now.AddDays(-31));

        await using ApplicationDbContext db = new(_options!);
        InstagramConnectionRepository repository = new(db);
        Assert.True(await repository.SaveRenewedTokenAsync(account.Id, "enc:token", "enc:renewed", now, now.AddDays(60)));
        await repository.MarkTokenRejectedAsync(account.Id, "enc:renewed", now);
        await repository.SaveAccountAsync(account.Id, account.OwnerUserId, account.AccountId!, "renewed", "enc:pasted", now.AddDays(60));

        InstagramConnection stored = await db.InstagramConnections.AsNoTracking().SingleAsync(c => c.Id == account.Id);
        Assert.Equal("enc:pasted", stored.AccessTokenEncrypted);
        Assert.Null(stored.AccessTokenRejectedAt);
        Assert.Equal(now.AddDays(60), stored.AccessTokenExpiresAt!.Value, TimeSpan.FromSeconds(1));
    }
}
