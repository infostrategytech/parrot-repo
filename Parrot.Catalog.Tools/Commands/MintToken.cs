using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Parrot.Catalog.Application.Security;

namespace Parrot.Catalog.Tools.Commands;

/// <summary>Development helper: a token the catalog accepts in Symmetric mode. In production tokens come from the identity provider.</summary>
internal static class MintToken
{
    public static int Run(IConfiguration configuration, Dictionary<string, string?> args)
    {
        Guid tenantId = Args.RequireGuid(args, "tenant");
        string scopes = Args.Require(args, "scopes");
        string sub = args.GetValueOrDefault("sub") ?? $"svc-{Guid.NewGuid():N}";
        int minutes = Args.Int(args, "minutes", 60);

        foreach (string s in CatalogScopes.ParseScopeClaim(scopes))
        {
            if (!CatalogScopes.All.Contains(s))
            {
                throw new ArgumentException($"Unknown scope '{s}'. Known: {string.Join(" ", CatalogScopes.All)}");
            }
        }

        string secret = configuration["Catalog:Auth:Secret"] ?? configuration["JwtSettings:Secret"] ?? throw new InvalidOperationException("Catalog:Auth:Secret or JwtSettings:Secret must be configured.");
        string issuer = configuration["Catalog:Auth:Issuer"] ?? configuration["JwtSettings:Issuer"] ?? "ParrotApi";
        string audience = configuration["Catalog:Auth:Audience"] ?? configuration["JwtSettings:Audience"] ?? "ParrotApp";

        DateTime now = DateTime.UtcNow;
        JwtSecurityToken token = new JwtSecurityToken(
            issuer,
            audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, sub),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(CatalogClaims.TenantId, tenantId.ToString("D")),
                new Claim(CatalogClaims.Scope, scopes),
            ],
            notBefore: now,
            expires: now.AddMinutes(minutes),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256));
        Console.WriteLine(new JwtSecurityTokenHandler().WriteToken(token));
        return 0;
    }
}
