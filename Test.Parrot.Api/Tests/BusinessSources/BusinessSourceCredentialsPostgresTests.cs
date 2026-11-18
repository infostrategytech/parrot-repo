using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Parrot.Application.Contracts.BusinessSources;
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

// Security and correctness of the credential broker against a real PostgreSQL database.
// PARROT_TEST_POSTGRES: a connection string whose user may create databases.
public sealed class BusinessSourceCredentialsPostgresTests : IAsyncLifetime
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
            new Workspace { Id = "ws-a", OwnerId = "owner" },
            new Workspace { Id = "ws-b", OwnerId = "owner" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private BusinessSourceCredentialsService Service(ApplicationDbContext db) =>
        new(db,
            new StubWhatsAppProtector(),
            new StubInstagramProtector(),
            new StubFacebookProtector(),
            Options.Create(new WhatsAppSettings()),
            Options.Create(new InstagramSettings()),
            Options.Create(new FacebookSettings()),
            new AppLogger<BusinessSourceCredentialsService>(NullLogger<BusinessSourceCredentialsService>.Instance));

    // Seeds one WhatsApp connection + source for workspace ws-a and returns the source id.
    private async Task<(Guid SourceId, Guid ConnectionId)> SeedWhatsAppAsync(
        string workspaceId, string token, bool activeSource = true)
    {
        await using ApplicationDbContext db = new(_options!);
        WhatsAppConnection connection = new()
        {
            WorkspaceId = workspaceId,
            Name = "Test connection",
            AccessTokenEncrypted = string.IsNullOrEmpty(token) ? null : $"enc:{token}",
            VerifyTokenEncrypted = "enc:verify",
        };
        db.WhatsAppConnections.Add(connection);
        BusinessSource source = new()
        {
            WorkspaceId = workspaceId,
            AuthorizationConnectionId = connection.Id,
            Channel = Channel.WhatsApp,
            Kind = BusinessSourceKind.Profile,
            ExternalId = "15550001234",
            IsActive = activeSource,
            MetadataSyncStatus = MetadataSyncStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.BusinessSources.Add(source);
        await db.SaveChangesAsync();
        return (source.Id, connection.Id);
    }

    // The critical tenant-isolation rule: another workspace's source id returns the same 404 as a random id.
    [PostgresFact]
    public async Task GetCredentialsAsync_WhenSourceFromAnotherWorkspace_ThrowsNotFoundException()
    {
        (Guid sourceId, _) = await SeedWhatsAppAsync("ws-b", "secret-token");

        await using ApplicationDbContext db = new(_options!);
        // ws-a calls with ws-b's source id.
        await Assert.ThrowsAsync<NotFoundException>(() => Service(db).GetCredentialsAsync(sourceId, "ws-a"));
    }

    [PostgresFact]
    public async Task GetCredentialsAsync_WhenSourceIsInactive_ThrowsNotFoundException()
    {
        (Guid sourceId, _) = await SeedWhatsAppAsync("ws-a", "secret-token", activeSource: false);

        await using ApplicationDbContext db = new(_options!);
        await Assert.ThrowsAsync<NotFoundException>(() => Service(db).GetCredentialsAsync(sourceId, "ws-a"));
    }

    [PostgresFact]
    public async Task GetCredentialsAsync_WhenNoTokenConfigured_ThrowsConflictException()
    {
        // Empty string token = connection exists but token not yet saved.
        (Guid sourceId, _) = await SeedWhatsAppAsync("ws-a", string.Empty);

        await using ApplicationDbContext db = new(_options!);
        await Assert.ThrowsAsync<ConflictException>(() => Service(db).GetCredentialsAsync(sourceId, "ws-a"));
    }

    [PostgresFact]
    public async Task GetCredentialsAsync_WhenWhatsAppSource_ReturnsDecryptedTokenAndApiVersion()
    {
        (Guid sourceId, _) = await SeedWhatsAppAsync("ws-a", "my-access-token");

        await using ApplicationDbContext db = new(_options!);
        BusinessSourceCredentialsResponse result = await Service(db).GetCredentialsAsync(sourceId, "ws-a");

        Assert.Equal("my-access-token", result.AccessToken);
        Assert.False(string.IsNullOrEmpty(result.ApiVersion));
    }

    // Stub protectors that strip the "enc:" prefix, mirroring the TestProtector pattern used in
    // FacebookSetupTests and InstagramSetupTests.
    private sealed class StubWhatsAppProtector : IWhatsAppTokenProtector
    {
        public string Protect(string token) => $"enc:{token}";
        public string Unprotect(string encryptedToken) => encryptedToken[4..];
    }

    private sealed class StubInstagramProtector : IInstagramCredentialProtector
    {
        public string Protect(string value) => $"enc:{value}";
        public string Unprotect(string protectedValue) => protectedValue[4..];
    }

    private sealed class StubFacebookProtector : IFacebookCredentialProtector
    {
        public string Protect(string value) => $"enc:{value}";
        public string Unprotect(string protectedValue) => protectedValue[4..];
    }
}
