using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Parrot.Catalog.Application.Tenancy;

namespace Parrot.Catalog.Infrastructure.Tenancy;

/// <summary>
/// Sets <c>app.tenant_id</c> on every connection the moment EF opens it, from the
/// verified token and nowhere else. Npgsql resets the session when the connection
/// goes back to the pool, so the value cannot outlive the request (ADR-004).
/// </summary>
public sealed class TenantConnectionInterceptor : DbConnectionInterceptor
{
    private const string Sql = "SELECT set_config('app.tenant_id', @tenant, false)";

    private readonly ITenantContext _tenant;

    public TenantConnectionInterceptor(ITenantContext tenant)
    {
        _tenant = tenant;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using DbCommand command = Build(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        using DbCommand command = Build(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand Build(DbConnection connection)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = Sql;
        NpgsqlParameter parameter = new NpgsqlParameter("tenant", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = _tenant.IsSet ? _tenant.TenantId.ToString("D") : string.Empty,
        };
        command.Parameters.Add(parameter);
        return command;
    }

    /// <summary>Refuses connection strings whose pooling behaviour would let a session setting leak between tenants.</summary>
    public static void AssertSafe(string connectionString, string name)
    {
        NpgsqlConnectionStringBuilder b = new NpgsqlConnectionStringBuilder(connectionString);
        if (b.NoResetOnClose)
        {
            throw new InvalidOperationException($"Catalog connection '{name}' sets 'No Reset On Close=true'; the tenant setting would survive pool reuse. Remove it.");
        }

        if (b.Multiplexing)
        {
            throw new InvalidOperationException($"Catalog connection '{name}' enables multiplexing; session settings are not supported in that mode. Remove it.");
        }
    }
}
