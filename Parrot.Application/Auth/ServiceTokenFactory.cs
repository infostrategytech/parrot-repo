using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Parrot.Application.Utils;

namespace Parrot.Application.Auth;

/// <summary>
/// Mints the token this API presents when it calls another service (the
/// orchestrator, the voice orientation) on behalf of one workspace.
/// </summary>
public interface IServiceTokenFactory
{
    /// <param name="ownerUserId">
    /// Despite its name, the workspace the call acts for: the session's
    /// <c>workspace_id</c>, never a person's id. The name dates from when one
    /// account was one business. A tenant's first workspace shares its id with
    /// the owner's account, so the two look alike there and differ everywhere else.
    /// </param>
    string Mint(string ownerUserId);

    /// <summary>
    /// Mints a token with <c>catalog:admin</c> scope for calling the Catalog service.
    /// </summary>
    string MintForCatalog(string ownerUserId);
}

/// <summary>
/// A service token is a workspace speaking, not a person: it carries no user
/// at all, so no route reads a person from it and no team access is checked
/// for it. It is signed with the session key (JwtSettings) and the session
/// audience, so the receiving service validates it the way it validates a
/// session, and it opens only routes that require <see cref="ServiceScopes.AgentService"/>,
/// a scope no session token ever has. It lives five minutes, not an hour,
/// and carries a fresh id, so a leaked one is worth little.
///
/// The orchestrator mints tokens of the same shape to call this API's
/// internal routes (<c>/api/internal/*</c>), which read the workspace from
/// <see cref="ServiceScopes.TenantClaim"/>. Its tokens need the same claims,
/// <c>svc</c> included; see the note on that claim below.
/// </summary>
public sealed class ServiceTokenFactory(IJwtHelper jwt) : IServiceTokenFactory
{
    /// <summary>The <c>svc</c> claim of tokens this API mints.</summary>
    public const string Service = "parrot-api";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public string Mint(string ownerUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Claim[] claims =
        [
            // The workspace twice, for two kinds of reader: services that take
            // the subject (as this API's own endpoints did when an account was
            // one business), and ones that take the tenant (the catalog, the
            // orchestrator). Both mean the workspace; neither is a person.
            new Claim(JwtRegisteredClaimNames.Sub, ownerUserId),
            new Claim(ServiceScopes.TenantClaim, ownerUserId),

            // The scope is what opens the internal routes (the ServiceScope
            // policy). The svc claim is what WorkspaceAccessFilter looks for to
            // treat the token as a service rather than a person, so it must be
            // present: without it the filter would look up the workspace id as
            // a user and refuse every workspace but a tenant's first.
            new Claim(ServiceScopes.ScopeClaim, ServiceScopes.AgentService),
            new Claim(ServiceScopes.ServiceClaim, Service),

            // A fresh id and the issue time: the orchestrator requires iat,
            // which the token helper does not add on its own.
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        ];

        return jwt.GenerateToken(claims, now.Add(Lifetime).UtcDateTime);
    }

    public string MintForCatalog(string ownerUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Claim[] claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, ownerUserId),
            new Claim(ServiceScopes.TenantClaim, ownerUserId),
            new Claim(ServiceScopes.ScopeClaim, ServiceScopes.CatalogAdmin),
            new Claim(ServiceScopes.ServiceClaim, Service),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        ];

        return jwt.GenerateToken(claims, now.Add(Lifetime).UtcDateTime);
    }
}
