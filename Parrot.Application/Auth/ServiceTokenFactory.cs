using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Parrot.Application.Utils;

namespace Parrot.Application.Auth;

/// <summary>Mints the token this API presents to the orchestrator when acting for an owner.</summary>
public interface IServiceTokenFactory
{
    string Mint(string ownerUserId);
}

/// <summary>
/// A service token names the owner three times on purpose: as the subject
/// (what this API's own endpoints read), as the tenant (what the catalog and
/// the orchestrator read) and as the minting service. It lives minutes, not
/// an hour, and carries an id, so a leaked one is worth little. The
/// orchestrator requires an issued-at claim, which the token helper does not
/// add on its own.
/// </summary>
public sealed class ServiceTokenFactory(IJwtHelper jwt) : IServiceTokenFactory
{
    public const string Service = "parrot-api";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public string Mint(string ownerUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Claim[] claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, ownerUserId),
            new Claim(ServiceScopes.TenantClaim, ownerUserId),
            new Claim(ServiceScopes.ScopeClaim, ServiceScopes.AgentService),
            new Claim(ServiceScopes.ServiceClaim, Service),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        ];

        return jwt.GenerateToken(claims, now.Add(Lifetime).UtcDateTime);
    }
}
