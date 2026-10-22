using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Parrot.Catalog.Application.Security;

namespace Parrot.Catalog.Api.Security;

/// <summary>
/// A Parrot session token carries catalog scopes worked out when it was
/// issued, and lives for an hour. A team member's access can change or end
/// sooner, so each request made with one is checked against Parrot's API,
/// which reads the member's grant now: the token's scopes are replaced with
/// the current ones, and a session that has lost the workspace is refused.
///
/// Only session tokens are checked (they name a workspace); service tokens,
/// such as the AI agent's, carry their own fixed scopes. Without
/// <see cref="CatalogAuthOptions.AccessCheckUrl"/> nothing is checked and the
/// token's scopes stand, as before.
/// </summary>
public sealed class SessionAccessCheck(IHttpClientFactory http, IMemoryCache cache, CatalogAuthOptions options, ILogger<SessionAccessCheck> logger)
{
    public const string HttpClientName = "parrot-session-access";

    /// <summary>The claim only Parrot session tokens carry.</summary>
    public const string WorkspaceClaim = "workspace_id";

    /// <summary>What the API answered, or why the request must be refused.</summary>
    public sealed record Verdict(string? CatalogScopes, string? RefusedBecause)
    {
        public static Verdict Refuse(string reason) => new(null, reason);
    }

    private sealed record AccessAnswer(string WorkspaceId, string CatalogScopes);

    public static bool Applies(ClaimsPrincipal principal) => principal.FindFirst(WorkspaceClaim) is not null;

    public async Task<Verdict> CheckAsync(ClaimsPrincipal principal, string rawToken, CancellationToken cancellationToken)
    {
        string? tenant = principal.FindFirst(CatalogClaims.TenantId)?.Value;
        string key = "session-access:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        if (options.AccessCheckCacheSeconds > 0 && cache.TryGetValue(key, out Verdict? cached) && cached is not null)
        {
            return cached;
        }

        Verdict verdict = await AskAsync(rawToken, tenant, cancellationToken);
        if (options.AccessCheckCacheSeconds > 0 && verdict.RefusedBecause is null)
        {
            cache.Set(key, verdict, TimeSpan.FromSeconds(options.AccessCheckCacheSeconds));
        }

        return verdict;
    }

    /// <summary>The token's scope claims replaced with the ones that hold now.</summary>
    public static void ApplyScopes(ClaimsPrincipal principal, string catalogScopes)
    {
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        foreach (Claim stale in identity.FindAll(c => c.Type is CatalogClaims.Scope or CatalogClaims.ScopeAlt).ToList())
        {
            identity.RemoveClaim(stale);
        }

        if (!string.IsNullOrWhiteSpace(catalogScopes))
        {
            identity.AddClaim(new Claim(CatalogClaims.Scope, catalogScopes));
        }
    }

    private async Task<Verdict> AskAsync(string rawToken, string? tenant, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, "api/workspaces/current/access");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
            using HttpResponseMessage response = await http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                return Verdict.Refuse("The session no longer has access to this workspace.");
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("session_access.check_failed Status={Status}", (int)response.StatusCode);
                return Verdict.Refuse("Access could not be checked. Try again.");
            }

            AccessAnswer? answer = await response.Content.ReadFromJsonAsync<AccessAnswer>(cancellationToken);

            // The catalog tenant is the workspace; an answer about another one is not an answer.
            if (answer is null || !string.Equals(answer.WorkspaceId, tenant, StringComparison.OrdinalIgnoreCase))
            {
                return Verdict.Refuse("The session's workspace does not match its catalog.");
            }

            return new Verdict(answer.CatalogScopes, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Refused rather than trusting the token: a stale token must not win because Parrot is unreachable.
            logger.LogWarning(ex, "session_access.unreachable; the request is refused");
            return Verdict.Refuse("Access could not be checked. Try again.");
        }
    }
}
