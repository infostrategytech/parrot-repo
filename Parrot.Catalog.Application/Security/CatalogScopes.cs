namespace Parrot.Catalog.Application.Security;

public static class CatalogScopes
{
    public const string Read = "catalog:read";
    public const string Write = "catalog:write";
    public const string Publish = "catalog:publish";
    public const string Admin = "catalog:admin";
    public const string AiTools = "catalog:ai-tools";

    /// <summary>Holding, committing and releasing stock for payment orders. Granted to Parrot alone; nothing implies it.</summary>
    public const string Orders = "catalog:orders";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Read, Write, Publish, Admin, AiTools, Orders,
    };

    /// <summary>The scopes that satisfy a given requirement. Admin implies read, write and publish; nothing implies ai-tools.</summary>
    public static IReadOnlyList<string> Satisfying(string required) => required switch
    {
        Read => [Read, Admin],
        Write => [Write, Admin],
        Publish => [Publish, Admin],
        Admin => [Admin],
        AiTools => [AiTools],
        Orders => [Orders],
        _ => throw new ArgumentOutOfRangeException(nameof(required), required, "Unknown catalog scope."),
    };

    public static bool Grants(IReadOnlySet<string> held, string required)
        => Satisfying(required).Any(held.Contains);

    public static IEnumerable<string> ParseScopeClaim(string? spaceSeparated)
        => string.IsNullOrWhiteSpace(spaceSeparated)
            ? []
            : spaceSeparated.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class CatalogPolicies
{
    public const string Read = "catalog.read";
    public const string Write = "catalog.write";
    public const string Publish = "catalog.publish";
    public const string Admin = "catalog.admin";
    public const string AiTools = "catalog.ai-tools";
    public const string Orders = "catalog.orders";
}

public static class CatalogClaims
{
    public const string TenantId = "tenant_id";

    /// <summary>The account that owns the workspace. Optional: service tokens don't carry it.</summary>
    public const string OwnerId = "owner_id";
    public const string Scope = "scope";
    public const string ScopeAlt = "scp";
}
