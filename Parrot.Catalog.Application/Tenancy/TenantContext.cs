namespace Parrot.Catalog.Application.Tenancy;

/// <summary>
/// Who is acting and for which tenant, as read from the verified token. The only
/// writer is the authentication middleware; nothing downstream may change it, and
/// nothing may fill it from a request body or an LLM argument (ADR-003).
/// </summary>
public interface ITenantContext
{
    bool IsSet { get; }

    Guid TenantId { get; }

    string Actor { get; }

    string RequestId { get; }

    IReadOnlySet<string> Scopes { get; }

    bool HasScope(string scope);
}

public sealed class TenantContext : ITenantContext
{
    private Guid _tenantId;

    public bool IsSet { get; private set; }

    public Guid TenantId => IsSet ? _tenantId : throw new InvalidOperationException("Tenant context has not been established for this request.");

    public string Actor { get; private set; } = string.Empty;

    public string RequestId { get; private set; } = string.Empty;

    public IReadOnlySet<string> Scopes { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    public bool HasScope(string scope) => Scopes.Contains(scope);

    public void Establish(Guid tenantId, string actor, IEnumerable<string> scopes, string requestId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        _tenantId = tenantId;
        Actor = actor;
        RequestId = requestId;
        Scopes = new HashSet<string>(scopes, StringComparer.Ordinal);
        IsSet = true;
    }
}
