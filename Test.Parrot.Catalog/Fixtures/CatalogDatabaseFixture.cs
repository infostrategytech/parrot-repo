using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Catalog.Infrastructure.Data;

namespace Test.Parrot.Catalog.Fixtures;

/// <summary>
/// One throwaway database per test run, owned by a non-superuser migrator, with login
/// users that are members of the three group roles. Connecting as real roles is the
/// point: an isolation test run as a superuser would prove nothing (ADR-004).
/// </summary>
public sealed class CatalogDatabaseFixture : IAsyncLifetime
{
    private readonly string? _adminConnection = Environment.GetEnvironmentVariable(PostgresFactAttribute.EnvVar);
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];

    public bool Available => _adminConnection is not null;

    public string DatabaseName => $"ct_{_suffix}";

    public string Migrator { get; private set; } = string.Empty;

    public string Writer { get; private set; } = string.Empty;

    public string Reader { get; private set; } = string.Empty;

    public string Relay { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (_adminConnection is null)
        {
            return;
        }

        NpgsqlConnectionStringBuilder admin = new NpgsqlConnectionStringBuilder(_adminConnection);
        await using (NpgsqlConnection conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await Exec(conn, """
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'catalog_writer') THEN CREATE ROLE catalog_writer NOLOGIN; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'catalog_reader') THEN CREATE ROLE catalog_reader NOLOGIN; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'catalog_outbox_relay') THEN CREATE ROLE catalog_outbox_relay NOLOGIN; END IF;
                END $$;
                """);
            await Exec(conn, $"CREATE ROLE {Role("migrator")} LOGIN PASSWORD 'pw'");
            await Exec(conn, $"CREATE ROLE {Role("writer")} LOGIN PASSWORD 'pw' IN ROLE catalog_writer");
            await Exec(conn, $"CREATE ROLE {Role("reader")} LOGIN PASSWORD 'pw' IN ROLE catalog_reader");
            await Exec(conn, $"CREATE ROLE {Role("relay")} LOGIN PASSWORD 'pw' IN ROLE catalog_outbox_relay");
            await Exec(conn, $"CREATE DATABASE {DatabaseName} OWNER {Role("migrator")}");
        }

        NpgsqlConnectionStringBuilder db = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = DatabaseName };
        await using (NpgsqlConnection conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await Exec(conn, "CREATE EXTENSION IF NOT EXISTS btree_gist");
            await Exec(conn, $"GRANT CONNECT ON DATABASE {DatabaseName} TO {Role("writer")}, {Role("reader")}, {Role("relay")}");
        }

        Migrator = Build(db, Role("migrator"));
        Writer = Build(db, Role("writer"));
        Reader = Build(db, Role("reader"));
        Relay = Build(db, Role("relay"));

        DbContextOptions<CatalogDbContext> options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(Migrator, n => n.MigrationsHistoryTable("__ef_migrations_history", "catalog"))
            .Options;
        await using CatalogDbContext context = new CatalogDbContext(options);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_adminConnection is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using NpgsqlConnection conn = new NpgsqlConnection(_adminConnection);
        await conn.OpenAsync();
        await Exec(conn, $"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)");
        foreach (string role in new[] { "writer", "reader", "relay", "migrator" })
        {
            await Exec(conn, $"DROP ROLE IF EXISTS {Role(role)}");
        }
    }

    /// <summary>Runs SQL as the given role with the tenant set, for assertions that bypass the API.</summary>
    public async Task<T> ScalarAsync<T>(string connectionString, Guid? tenantId, string sql, params NpgsqlParameter[] parameters)
    {
        await using NpgsqlConnection conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        if (tenantId is { } t)
        {
            await using NpgsqlCommand set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", conn);
            set.Parameters.AddWithValue("t", t.ToString("D"));
            await set.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        object? result = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result, typeof(T))!;
    }

    public async Task ExecAsync(string connectionString, Guid? tenantId, string sql)
    {
        await using NpgsqlConnection conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        if (tenantId is { } t)
        {
            await using NpgsqlCommand set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", conn);
            set.Parameters.AddWithValue("t", t.ToString("D"));
            await set.ExecuteNonQueryAsync();
        }

        await Exec(conn, sql);
    }

    private string Role(string kind) => $"ct_{_suffix}_{kind}";

    private static string Build(NpgsqlConnectionStringBuilder template, string user) => new NpgsqlConnectionStringBuilder(template.ConnectionString)
    {
        Username = user,
        Password = "pw",
        Pooling = true,
        MaxPoolSize = 20,
        IncludeErrorDetail = true,
    }.ConnectionString;

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using NpgsqlCommand cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
