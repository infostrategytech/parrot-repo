using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Parrot.Catalog.Application.Security;

namespace Parrot.Catalog.Api.Security;

public static class CatalogAuthentication
{
    public static CatalogAuthOptions ResolveAuthOptions(IConfiguration configuration, IHostEnvironment environment)
    {
        CatalogAuthOptions options = configuration.GetSection(CatalogAuthOptions.SectionName).Get<CatalogAuthOptions>() ?? new CatalogAuthOptions();
        if (!options.IsOidc)
        {
            options.Secret ??= configuration["JwtSettings:Secret"];
            options.Issuer ??= configuration["JwtSettings:Issuer"];
            options.Audience ??= configuration["JwtSettings:Audience"];
            if (string.IsNullOrWhiteSpace(options.Secret) || string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience))
            {
                throw new InvalidOperationException("Catalog:Auth (or JwtSettings) must provide Secret, Issuer and Audience in Symmetric mode.");
            }

            if (!environment.IsDevelopment() && (options.Secret == CatalogAuthOptions.PlaceholderSecret || options.Secret.Length < 32))
            {
                throw new InvalidOperationException("Catalog:Auth:Secret must be a real secret of at least 32 characters outside Development.");
            }
        }
        else if (string.IsNullOrWhiteSpace(options.Authority) || string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new InvalidOperationException("Catalog:Auth must provide Authority and Audience in Oidc mode.");
        }

        return options;
    }

    public static IServiceCollection AddCatalogAuthentication(this IServiceCollection services, CatalogAuthOptions options)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidAudience = options.Audience,
                    ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),
                    NameClaimType = "sub",
                };

                if (options.IsOidc)
                {
                    jwt.Authority = options.Authority;
                    jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                    jwt.TokenValidationParameters.ValidIssuer = options.Issuer ?? options.Authority;
                }
                else
                {
                    jwt.TokenValidationParameters.ValidIssuer = options.Issuer;
                    jwt.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret!));
                    jwt.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.HmacSha256];
                }
            });

        services.AddAuthorizationBuilder()
            // Deny by default: every request needs a verified token carrying a tenant.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => TenantIdOf(ctx.User) is not null)
                .Build())
            .AddPolicy(CatalogPolicies.Read, p => Scope(p, CatalogScopes.Read))
            .AddPolicy(CatalogPolicies.Write, p => Scope(p, CatalogScopes.Write))
            .AddPolicy(CatalogPolicies.Publish, p => Scope(p, CatalogScopes.Publish))
            .AddPolicy(CatalogPolicies.Admin, p => Scope(p, CatalogScopes.Admin))
            .AddPolicy(CatalogPolicies.AiTools, p => Scope(p, CatalogScopes.AiTools));

        return services;
    }

    public static Guid? TenantIdOf(ClaimsPrincipal user)
    {
        string? raw = user.FindFirst(CatalogClaims.TenantId)?.Value;
        return Guid.TryParse(raw, out Guid id) && id != Guid.Empty ? id : null;
    }

    public static IReadOnlySet<string> ScopesOf(ClaimsPrincipal user)
    {
        HashSet<string> scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (Claim claim in user.FindAll(c => c.Type is CatalogClaims.Scope or CatalogClaims.ScopeAlt))
        {
            foreach (string s in CatalogScopes.ParseScopeClaim(claim.Value))
            {
                if (CatalogScopes.All.Contains(s))
                {
                    scopes.Add(s);
                }
            }
        }

        return scopes;
    }

    private static void Scope(AuthorizationPolicyBuilder policy, string required)
        => policy.RequireAuthenticatedUser()
            .RequireAssertion(ctx => TenantIdOf(ctx.User) is not null && CatalogScopes.Grants(ScopesOf(ctx.User), required));
}
