namespace Parrot.Catalog.Tools.Commands;

internal static class Args
{
    public const string Usage = """
        Parrot.Catalog.Tools <command> [options]

          migrate-db                                   Apply EF migrations with Catalog:ConnectionStrings:Migrator.
          seed --tenant <uuid> [--catalog main]        Seed the reference catalog (4 types, products, offers, prices) and publish it.
          migrate-type-version --tenant <uuid> --type <key> --from <n> --to <n> [--dry-run]
                                                       Move products between type versions; dry run only reports.
          mint-token --tenant <uuid> --scopes "catalog:ai-tools" [--sub <id>] [--minutes 60]
                                                       Mint a development JWT with the configured symmetric secret.
          purge-tenant --tenant <uuid> --confirm <uuid> [--keep-audit]
                                                       Hard-delete every row of a tenant (offboarding). Needs the migrator connection.
          sweep-outbox [--days 7]                      Delete published outbox rows older than the retention window.
          reindex --tenant <uuid>                      Rebuild the derived attribute index and documents for a tenant.

        Connection strings come from configuration (appsettings.json, environment: Catalog__ConnectionStrings__Writer, ...).
        """;

    public static Dictionary<string, string?> Parse(string[] args, out string? command)
    {
        Dictionary<string, string?> result = new Dictionary<string, string?>(StringComparer.Ordinal);
        command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        for (int i = command is null ? 0 : 1; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key = a[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = args[++i];
            }
            else
            {
                result[key] = "true";
            }
        }

        return result;
    }

    public static Guid RequireGuid(Dictionary<string, string?> args, string key)
    {
        if (!args.TryGetValue(key, out string? value) || !Guid.TryParse(value, out Guid id) || id == Guid.Empty)
        {
            throw new ArgumentException($"--{key} <uuid> is required.");
        }

        return id;
    }

    public static string Require(Dictionary<string, string?> args, string key)
        => args.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"--{key} is required.");

    public static int Int(Dictionary<string, string?> args, string key, int fallback)
        => args.TryGetValue(key, out string? value) && int.TryParse(value, out int n) ? n : fallback;

    public static bool Flag(Dictionary<string, string?> args, string key) => args.ContainsKey(key);
}
