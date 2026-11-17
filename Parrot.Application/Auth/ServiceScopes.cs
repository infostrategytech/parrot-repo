using System.Security.Claims;

namespace Parrot.Application.Auth;

/// <summary>
/// The scopes a service token carries, and how they are read. A service
/// token is one Parrot and the orchestrator mint for each other with the
/// shared JwtSettings: the same signature as a session token, but a short
/// life, a tenant, and a scope no session token ever has.
/// </summary>
public static class ServiceScopes
{
    public const string ScopeClaim = "scope";
    public const string ScopeClaimAlt = "scp";
    public const string TenantClaim = "tenant_id";
    public const string ServiceClaim = "svc";

    /// <summary>Lets a service act for a tenant: resolve its agent, read its conversations, decide its approvals.</summary>
    public const string AgentService = "agent:service";

    /// <summary>Lets the main API read and write the catalog on behalf of a tenant.</summary>
    public const string CatalogAdmin = "catalog:admin";

    /// <summary>
    /// Lets the main API call the catalog's AI tools (live prices, search) for a tenant,
    /// on behalf of the tenant's agent. Read-only, and nothing else in the catalog opens to it.
    /// </summary>
    public const string CatalogAiTools = "catalog:ai-tools";

    /// <summary>Holding, committing and releasing stock for payment orders.</summary>
    public const string CatalogOrders = "catalog:orders";

    /// <summary>The authorization policy that requires <see cref="AgentService"/>.</summary>
    public const string PolicyName = "ServiceScope";

    public static IEnumerable<string> Parse(string? spaceSeparated) =>
        string.IsNullOrWhiteSpace(spaceSeparated)
            ? []
            : spaceSeparated.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool HasScope(ClaimsPrincipal user, string required) =>
        user.FindAll(c => c.Type is ScopeClaim or ScopeClaimAlt)
            .SelectMany(c => Parse(c.Value))
            .Contains(required, StringComparer.Ordinal);
}
