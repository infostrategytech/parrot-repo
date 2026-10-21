using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Conversations;
using Parrot.Infrastructure.Data;
using static Test.Parrot.Api.Tests.Services.Integrations.ConnectionMessageReceiptPostgresTests;

namespace Test.Parrot.Api.Tests.Services.Integrations;

// The message worker asks Meta for the name behind a Messenger PSID or an Instagram IGSID, with the
// connection's token read from PostgreSQL. Meta is faked; PostgreSQL is real (PARROT_TEST_POSTGRES).
public class MetaContactNamesTests : IAsyncLifetime
{
    private sealed class Contexts(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeMeta(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class PlainFacebook : IFacebookCredentialProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class PlainInstagram : IInstagramCredentialProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private const string Customer = "6215837091842310";
    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
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
        _cache.Dispose();
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private MetaContactNames Lookup(FakeMeta meta) => new(
        new Clients(meta), new Contexts(_options!), new PlainFacebook(), new PlainInstagram(),
        Options.Create(new FacebookSettings()), Options.Create(new InstagramSettings()), _cache,
        NullLogger<MetaContactNames>.Instance);

    private async Task<Guid> PageAsync(DateTime? disabledAt = null)
    {
        await using ApplicationDbContext db = new(_options!);
        ApplicationUser user = new() { UserName = "owner@example.com", Email = "owner@example.com" };
        db.Users.Add(user);
        // A tenant's first workspace takes the user's id, as sign-in makes it.
        db.Workspaces.Add(new Workspace { Id = user.Id, OwnerId = user.Id });
        FacebookConnection page = new()
        {
            WorkspaceId = user.Id, VerifyTokenEncrypted = "encrypted", PageId = "185860297939218",
            AccessTokenEncrypted = "page-token", WebhookVerifiedAt = DateTime.UtcNow, DisabledAt = disabledAt,
        };
        db.FacebookConnections.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    }

    private async Task<Guid> AccountAsync()
    {
        await using ApplicationDbContext db = new(_options!);
        ApplicationUser user = new() { UserName = "ig@example.com", Email = "ig@example.com" };
        db.Users.Add(user);
        // A tenant's first workspace takes the user's id, as sign-in makes it.
        db.Workspaces.Add(new Workspace { Id = user.Id, OwnerId = user.Id });
        InstagramConnection account = new()
        {
            WorkspaceId = user.Id, VerifyTokenEncrypted = "encrypted", AccountId = "17841400000000000",
            AccessTokenEncrypted = "ig-token", WebhookVerifiedAt = DateTime.UtcNow,
        };
        db.InstagramConnections.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static IncomingMessage Message(MessagingPlatform platform, Guid connection) => new()
    {
        ConnectionId = connection, OwnerUserId = "owner", MessageId = "m_1", Platform = platform,
        ExternalAccountId = "a", From = Customer, Timestamp = "1791447800000", Type = "text",
        ChannelId = "a", ChannelDisplayName = "", ReceivedAt = DateTime.UtcNow,
    };

    [PostgresFact]
    public async Task AMessengerCustomer_IsNamedByTheirProfile_WithThePageToken()
    {
        FakeMeta meta = new(HttpStatusCode.OK, """{"name":"Ada Obi","first_name":"Ada","last_name":"Obi","id":"6215837091842310"}""");

        string? name = (await Lookup(meta).FindAsync(Message(MessagingPlatform.Facebook, await PageAsync()), CancellationToken.None))?.Name;

        Assert.Equal("Ada Obi", name);
        HttpRequestMessage request = Assert.Single(meta.Requests);
        Assert.Equal("graph.facebook.com", request.RequestUri!.Host);
        Assert.EndsWith("/" + Customer, request.RequestUri.AbsolutePath);
        Assert.Equal("page-token", request.Headers.Authorization!.Parameter);
    }

    [PostgresFact]
    public async Task AMessengerProfileWithoutAFullName_IsNamedByFirstAndLast()
    {
        FakeMeta meta = new(HttpStatusCode.OK, """{"first_name":"Ada","last_name":"Obi"}""");

        Assert.Equal("Ada Obi", (await Lookup(meta).FindAsync(Message(MessagingPlatform.Facebook, await PageAsync()), CancellationToken.None))?.Name);
    }

    // Instagram users often leave their name empty; the handle is what the business knows them by.
    [PostgresFact]
    public async Task AnInstagramCustomerWithoutAName_IsNamedByTheirHandle()
    {
        FakeMeta meta = new(HttpStatusCode.OK, """{"username":"ada.obi","id":"6215837091842310"}""");

        global::Parrot.Application.Messaging.ContactProfile? profile = await Lookup(meta).FindAsync(Message(MessagingPlatform.Instagram, await AccountAsync()), CancellationToken.None);

        Assert.Equal("@ada.obi", profile?.Name);
        Assert.Equal("ada.obi", profile?.Username);
        Assert.Equal("graph.instagram.com", Assert.Single(meta.Requests).RequestUri!.Host);
    }

    // Each customer is asked for once, not once per message.
    [PostgresFact]
    public async Task ASecondMessageFromTheSameCustomer_UsesTheNameAlreadyFound()
    {
        FakeMeta meta = new(HttpStatusCode.OK, """{"name":"Ada Obi"}""");
        MetaContactNames lookup = Lookup(meta);
        Guid page = await PageAsync();

        await lookup.FindAsync(Message(MessagingPlatform.Facebook, page), CancellationToken.None);
        Assert.Equal("Ada Obi", (await lookup.FindAsync(Message(MessagingPlatform.Facebook, page), CancellationToken.None))?.Name);

        Assert.Single(meta.Requests);
    }

    // No permission, or a person Meta will not describe: no name, and not asked again right away.
    [PostgresFact]
    public async Task ARefusalLeavesTheCustomerUnnamed_AndIsNotRetriedOnTheNextMessage()
    {
        FakeMeta meta = new(HttpStatusCode.BadRequest, """{"error":{"code":100,"message":"Unsupported get request."}}""");
        MetaContactNames lookup = Lookup(meta);
        Guid page = await PageAsync();

        Assert.Null(await lookup.FindAsync(Message(MessagingPlatform.Facebook, page), CancellationToken.None));
        Assert.Null(await lookup.FindAsync(Message(MessagingPlatform.Facebook, page), CancellationToken.None));

        Assert.Single(meta.Requests);
    }

    [PostgresFact]
    public async Task AnOutageNeverFailsTheMessage()
    {
        FakeMeta meta = new(HttpStatusCode.ServiceUnavailable, "");

        Assert.Null(await Lookup(meta).FindAsync(Message(MessagingPlatform.Facebook, await PageAsync()), CancellationToken.None));
    }

    [PostgresFact]
    public async Task ADisconnectedPage_IsNotAsked()
    {
        FakeMeta meta = new(HttpStatusCode.OK, """{"name":"Ada Obi"}""");

        Assert.Null(await Lookup(meta).FindAsync(Message(MessagingPlatform.Facebook, await PageAsync(DateTime.UtcNow)), CancellationToken.None));
        Assert.Empty(meta.Requests);
    }

    [Fact]
    public async Task OtherChannels_AreNotLookedUp()
    {
        FakeMeta meta = new(HttpStatusCode.OK, """{"name":"Ada Obi"}""");
        MetaContactNames lookup = new(
            new Clients(meta), Mock.Of<IDbContextFactory<ApplicationDbContext>>(), new PlainFacebook(), new PlainInstagram(),
            Options.Create(new FacebookSettings()), Options.Create(new InstagramSettings()), _cache,
            NullLogger<MetaContactNames>.Instance);

        Assert.Null(await lookup.FindAsync(Message(MessagingPlatform.WhatsApp, Guid.NewGuid()), CancellationToken.None));
        Assert.Empty(meta.Requests);
    }
}
