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
          publish-items --tenant <uuid>[,<uuid>...] [--owner <user id>] [--batch 100] [--dry-run]
                                                       Write one ItemChanged/ItemRemoved per item to the outbox for catalog.items
                                                       (backfill). The relay publishes them. Deleted items are skipped.
                                                       --tenant takes workspace ids. --owner is the account that owns them
                                                       (Workspace.OwnerId), sent as tenant_id and recorded for later events;
                                                       without it, the owner the catalog already recorded is used, if any.

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

    /// <summary>One or more comma-separated tenant ids, for commands that can run over several.</summary>
    public static List<Guid> RequireGuids(Dictionary<string, string?> args, string key)
    {
        string raw = args.TryGetValue(key, out string? value) ? value ?? string.Empty : string.Empty;
        List<Guid> ids = [];
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out Guid id) || id == Guid.Empty)
            {
                throw new ArgumentException($"--{key} has an invalid uuid: '{part}'.");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids.Count > 0 ? ids : throw new ArgumentException($"--{key} <uuid>[,<uuid>...] is required.");
    }

    public static string Require(Dictionary<string, string?> args, string key)
        => args.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"--{key} is required.");

    public static int Int(Dictionary<string, string?> args, string key, int fallback)
        => args.TryGetValue(key, out string? value) && int.TryParse(value, out int n) ? n : fallback;

    public static bool Flag(Dictionary<string, string?> args, string key) => args.ContainsKey(key);
}
