using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Parrot.Application.Auth;
using Parrot.Application.Utils;

namespace Test.Parrot.Api.Tests.Services.Agents;

// The orchestrator verifies these with the same secret, issuer and audience
// as a session token, and reads sub, tenant_id, scope, svc, jti and iat.
public class ServiceTokenFactoryTests
{
    private const string Secret = "super-secret-key-that-is-at-least-32-chars!!";
    private const string Issuer = "ParrotApi";
    private const string Audience = "ParrotApp";
    private const string Owner = "owner-1";

    private static ServiceTokenFactory CreateSut() =>
        new(new JwtHelper(Options.Create(new JwtSettings { Secret = Secret, Issuer = Issuer, Audience = Audience, ExpirationInMinutes = 60 })));

    private static ClaimsPrincipal Validate(string token, out SecurityToken validated) =>
        new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.Zero,
        }, out validated);

    [Fact]
    public void Mint_ProducesATokenTheSharedSecretVerifies_NamingTheOwnerAsSubjectAndTenant()
    {
        string token = CreateSut().Mint(Owner);

        ClaimsPrincipal principal = Validate(token, out _);

        Assert.Equal(Owner, principal.FindFirstValue(JwtRegisteredClaimNames.Sub));
        Assert.Equal(Owner, principal.FindFirstValue(ServiceScopes.TenantClaim));
        Assert.Equal(ServiceScopes.AgentService, principal.FindFirstValue(ServiceScopes.ScopeClaim));
        Assert.Equal(ServiceTokenFactory.Service, principal.FindFirstValue(ServiceScopes.ServiceClaim));
        Assert.False(string.IsNullOrEmpty(principal.FindFirstValue(JwtRegisteredClaimNames.Jti)));
    }

    [Fact]
    public void Mint_LivesMinutesNotAnHour()
    {
        DateTime before = DateTime.UtcNow;

        string token = CreateSut().Mint(Owner);

        JwtSecurityToken decoded = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.InRange(decoded.ValidTo, before.Add(ServiceTokenFactory.Lifetime).AddSeconds(-2), DateTime.UtcNow.Add(ServiceTokenFactory.Lifetime).AddSeconds(2));
    }

    // PyJWT requires iat and refuses it as a string.
    [Fact]
    public void Mint_WritesIssuedAtAsANumber()
    {
        string token = CreateSut().Mint(Owner);

        JwtSecurityToken decoded = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.True(decoded.Payload.TryGetValue(JwtRegisteredClaimNames.Iat, out object? iat));
        Assert.True(iat is long or int, $"iat was {iat?.GetType().Name}");
        Assert.InRange(decoded.IssuedAt, DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void Mint_IsRejectedByAnotherSecret()
    {
        string token = CreateSut().Mint(Owner);

        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(() =>
            new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("another-secret-that-is-also-long-enough-0123456789")),
            }, out _));
    }

    [Fact]
    public void Mint_WithNoOwner_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateSut().Mint(" "));
    }

    [Fact]
    public void MintForCatalogAiTools_GrantsOnlyTheAiToolsScope_ForTheWorkspace()
    {
        ClaimsPrincipal principal = Validate(CreateSut().MintForCatalogAiTools(Owner), out SecurityToken validated);

        Assert.Equal(Owner, principal.FindFirstValue(ServiceScopes.TenantClaim));
        Assert.Equal(ServiceScopes.CatalogAiTools, Assert.Single(principal.FindAll(ServiceScopes.ScopeClaim)).Value);
        Assert.Equal(ServiceTokenFactory.Service, principal.FindFirstValue(ServiceScopes.ServiceClaim));
        Assert.True(validated.ValidTo <= DateTime.UtcNow.Add(ServiceTokenFactory.Lifetime).AddSeconds(5));
    }
}
