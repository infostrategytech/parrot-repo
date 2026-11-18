using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Parrot.Application.Auth;

namespace Parrot.Api.Security;

/// <summary>
/// Why a call to an internal endpoint (/api/internal, called by Omni with a service token) was turned away.
/// The caller only ever gets "Not found" or 403, which reads like a missing source; this says what was wrong
/// with the token: missing, expired, signed with another key, or without the scope or tenant. The response is
/// unchanged and the token itself is never logged.
/// </summary>
public static class InternalAuthLog
{
    private static readonly PathString Internal = "/api/internal";

    public static void AuthenticationFailed(AuthenticationFailedContext context)
    {
        if (!context.Request.Path.StartsWithSegments(Internal)) return;
        Logger(context.HttpContext).LogWarning(
            "internal.auth_failed Path={Path} Reason={Reason} Error={Error}",
            context.Request.Path.Value, Reason(context.Exception), context.Exception.GetType().Name);
    }

    // A failed token was logged above; a challenge with no failure means no token was sent at all.
    public static void Challenged(JwtBearerChallengeContext context)
    {
        if (!context.Request.Path.StartsWithSegments(Internal) || context.AuthenticateFailure is not null) return;
        Logger(context.HttpContext).LogWarning(
            "internal.auth_failed Path={Path} Reason={Reason} Error={Error}",
            context.Request.Path.Value, "no bearer token was sent", context.Error ?? "none");
    }

    // Signed correctly, but not a service token for a tenant.
    public static void Forbidden(ForbiddenContext context)
    {
        if (!context.Request.Path.StartsWithSegments(Internal)) return;
        System.Security.Claims.ClaimsPrincipal user = context.HttpContext.User;
        Logger(context.HttpContext).LogWarning(
            "internal.auth_forbidden Path={Path} Reason={Reason} Service={Service} Scopes={Scopes} HasTenant={HasTenant}",
            context.Request.Path.Value,
            ServiceScopes.HasScope(user, ServiceScopes.AgentService) ? "refused by the endpoint's policy" : $"the token has no {ServiceScopes.AgentService} scope",
            user.FindFirst(ServiceScopes.ServiceClaim)?.Value ?? "none",
            string.Join(' ', user.FindAll(c => c.Type is ServiceScopes.ScopeClaim or ServiceScopes.ScopeClaimAlt).Select(c => c.Value)),
            user.HasClaim(c => c.Type == ServiceScopes.TenantClaim));
    }

    private static string Reason(Exception exception) => exception switch
    {
        SecurityTokenExpiredException => "the token has expired",
        SecurityTokenInvalidSignatureException or SecurityTokenSignatureKeyNotFoundException =>
            "the signature doesn't match (Omni and Parrot are not using the same JwtSettings secret)",
        SecurityTokenInvalidIssuerException => "the issuer isn't accepted (JwtSettings issuer differs)",
        SecurityTokenInvalidAudienceException => "the audience isn't accepted (JwtSettings audience differs)",
        SecurityTokenNotYetValidException => "the token isn't valid yet (clocks differ)",
        _ => "the token couldn't be read",
    };

    private static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Parrot.Api.InternalAuth");
}
