using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Parrot.Catalog.Application.Security;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Tools.Commands;

namespace Test.Parrot.Catalog.Fixtures;

/// <summary>The API under test, wired to the fixture database and a test signing key, plus token and seeding helpers.</summary>
public sealed class CatalogApiFixture : IAsyncLifetime
{
    public const string Secret = "catalog-test-signing-secret-that-is-long-enough-0123456789";
    public const string Issuer = "ParrotApi";
    public const string Audience = "ParrotApp";

    public static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private WebApplicationFactory<Program>? _factory;

    public CatalogDatabaseFixture Database { get; } = new CatalogDatabaseFixture();

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Fixture not initialised.");

    public Guid TenantA { get; } = Guid.NewGuid();

    public Guid TenantB { get; } = Guid.NewGuid();

    /// <summary>The bucket items' photos go in, in memory.</summary>
    public FakePhotoStorage Photos { get; } = new FakePhotoStorage();

    public SeedResult? SeedA { get; private set; }

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        if (!Database.Available)
        {
            return;
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Catalog:ConnectionStrings:Writer", Database.Writer);
            b.UseSetting("Catalog:ConnectionStrings:Reader", Database.Reader);
            b.UseSetting("Catalog:ConnectionStrings:Relay", Database.Relay);
            b.UseSetting("Catalog:ConnectionStrings:Migrator", Database.Migrator);
            b.UseSetting("Catalog:ApplyMigrations", "false");
            b.UseSetting("Catalog:Cache:Provider", "Memory");
            b.UseSetting("Catalog:Relay:InProcess", "false");
            b.UseSetting("Catalog:Auth:Mode", "Symmetric");
            b.UseSetting("Catalog:Auth:Secret", Secret);
            b.UseSetting("Catalog:Auth:Issuer", Issuer);
            b.UseSetting("Catalog:Auth:Audience", Audience);
            b.UseSetting("Catalog:RateLimits:ReadPerMinute", "100000");
            b.UseSetting("Catalog:RateLimits:SearchPerMinute", "100000");
            b.UseSetting("Catalog:RateLimits:WritePerMinute", "100000");
            b.UseSetting("Catalog:RateLimits:AiPerMinute", "100000");
            b.UseSetting("Catalog:RateLimits:TenantPerMinute", "1000000");
            b.ConfigureTestServices(services => services.AddSingleton<IItemPhotoStorage>(Photos));
        });

        SeedA = await SeedAsync(TenantA);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Database.DisposeAsync();
    }

    public HttpClient Client(Guid tenantId, params string[] scopes)
    {
        HttpClient client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(tenantId, scopes));
        return client;
    }

    public HttpClient Anonymous() => Factory.CreateClient();

    public static string Token(Guid? tenantId, string[] scopes, string? issuer = null, DateTime? expires = null, string? secret = null)
    {
        List<Claim> claims = [new Claim("sub", $"user-{tenantId:N}"), new Claim("jti", Guid.NewGuid().ToString("N"))];
        if (tenantId is { } t)
        {
            claims.Add(new Claim(CatalogClaims.TenantId, t.ToString("D")));
            claims.Add(new Claim(CatalogClaims.OwnerId, $"owner-{t:N}"));
        }

        if (scopes.Length > 0)
        {
            claims.Add(new Claim(CatalogClaims.Scope, string.Join(' ', scopes)));
        }

        JwtSecurityToken token = new JwtSecurityToken(
            issuer ?? Issuer,
            Audience,
            claims,
            notBefore: (expires is { } e && e < DateTime.UtcNow ? e : DateTime.UtcNow).AddMinutes(-1),
            expires: expires ?? DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret ?? Secret)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>A DI scope whose tenant context is already established, for calling services directly.</summary>
    public IServiceScope Scope(Guid tenantId, string actor = "test", string? ownerId = null)
    {
        IServiceScope scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Establish(tenantId, actor, CatalogScopes.All, Guid.NewGuid().ToString("N"), ownerId);
        return scope;
    }

    public async Task<SeedResult> SeedAsync(Guid tenantId, string catalogKey = "main")
    {
        using IServiceScope scope = Scope(tenantId, "seed");
        return await Seed.SeedAsync(scope.ServiceProvider, catalogKey, CancellationToken.None);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidOperationException($"Empty body: {body}");
    }

    public static HttpContent Body<T>(T value) => new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");

    public static HttpContent Raw(string json) => new StringContent(json, Encoding.UTF8, "application/json");
}

[CollectionDefinition(Name)]
public sealed class CatalogApiCollection : ICollectionFixture<CatalogApiFixture>
{
    public const string Name = "catalog-api";
}
