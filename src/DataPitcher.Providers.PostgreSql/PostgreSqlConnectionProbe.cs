using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlConnectionProbe : ICapabilityDetector
{
    public async Task<ConnectionProbeEvidence> ProbeAsync(ConnectionProbeRequest request, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(request.ResolvedConnectionString)
        {
            Timeout = 5,
            CommandTimeout = 5,
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var scalar = new NpgsqlCommand("SELECT 1;", connection) { CommandTimeout = 5 };
        _ = await scalar.ExecuteScalarAsync(cancellationToken);
        return await ReadEvidenceAsync(connection, request, cancellationToken);
    }

    private static async Task<ConnectionProbeEvidence> ReadEvidenceAsync(
        NpgsqlConnection connection, ConnectionProbeRequest request, CancellationToken cancellationToken)
    {
        var available = new HashSet<ConnectionCapability>
        {
            ConnectionCapability.CanConnect,
            ConnectionCapability.CanUseTransactions,
            ConnectionCapability.CanUseSnapshotIsolation,
        };
        var (databaseIdentity, providerVersion) = await ReadIdentityAsync(connection, cancellationToken);
        var permissions = await ReadPermissionsAsync(connection, request.Profile, cancellationToken);
        if (permissions.CanReadSchema)
            available.Add(ConnectionCapability.CanReadSchema);
        if (permissions.CanReadBusinessRows)
            available.Add(ConnectionCapability.CanReadBusinessRows);
        if (request.Role is ConnectionRole.Target && permissions.CanWriteBusinessRows)
        {
            available.Add(ConnectionCapability.CanBulkInsert);
            available.Add(ConnectionCapability.CanPreserveIdentity);
        }

        string? cleanupFailureCode = null;
        if (request.Mode is TransferMode.ResumableStaged && permissions.CanCreateStaging)
            cleanupFailureCode = await ProbeStagingAsync(connection, request, available, cancellationToken);
        if (request.Role is ConnectionRole.Source &&
            available.Contains(ConnectionCapability.CanCreateSourceStaging) &&
            available.Contains(ConnectionCapability.CanDropSourceStaging))
            available.Add(ConnectionCapability.SupportsDurableResume);
        return new ConnectionProbeEvidence(databaseIdentity, providerVersion, available, cleanupFailureCode);
    }

    private static async Task<(string DatabaseIdentity, string ProviderVersion)> ReadIdentityAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT current_database(), version();", connection) { CommandTimeout = 5 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<(bool CanReadSchema, bool CanReadBusinessRows, bool CanCreateStaging, bool CanWriteBusinessRows)> ReadPermissionsAsync(
        NpgsqlConnection connection, ConnectionProfile profile, CancellationToken cancellationToken)
    {
        const string sql = "SELECT has_database_privilege(current_database(), 'CONNECT'), " +
            "has_schema_privilege(@businessSchema, 'USAGE'), " +
            "COALESCE((SELECT bool_and(has_table_privilege(format('%I.%I', schemaname, tablename), 'SELECT')) FROM pg_tables WHERE schemaname=@businessSchema), false), " +
            "has_schema_privilege(@stagingSchema, 'CREATE'), " +
            "COALESCE((SELECT bool_and(has_table_privilege(format('%I.%I', schemaname, tablename), 'INSERT')) FROM pg_tables WHERE schemaname=@businessSchema), false);";
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("businessSchema", profile.BusinessSchema);
        command.Parameters.AddWithValue("stagingSchema", profile.StagingSchema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        _ = reader.GetBoolean(0);
        return (reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4));
    }

    private static async Task<string?> ProbeStagingAsync(
        NpgsqlConnection connection, ConnectionProbeRequest request, ISet<ConnectionCapability> available, CancellationToken cancellationToken)
    {
        var name = "dp_probe_" + Guid.NewGuid().ToString("N");
        var created = false;
        var verified = false;
        string? cleanupFailureCode = null;
        try
        {
            await ExecuteAsync(connection, "CREATE TABLE " + PostgreSqlIdentifier.Qualified(request.Profile.StagingSchema, name) + " (value integer);", cancellationToken);
            created = true;
            verified = await StagingObjectExistsAsync(connection, request.Profile.StagingSchema, name, cancellationToken);
            if (verified)
                available.Add(request.Role is ConnectionRole.Source
                    ? ConnectionCapability.CanCreateSourceStaging
                    : ConnectionCapability.CanCreateTargetStaging);
        }
        finally
        {
            if (created)
            {
                try
                {
                    await ExecuteAsync(connection, "DROP TABLE " + PostgreSqlIdentifier.Qualified(request.Profile.StagingSchema, name) + ";", cancellationToken);
                    if (verified)
                        available.Add(request.Role is ConnectionRole.Source
                            ? ConnectionCapability.CanDropSourceStaging
                            : ConnectionCapability.CanDropTargetStaging);
                }
                catch
                {
                    cleanupFailureCode = "staging_cleanup_failed";
                }
            }
        }
        return cleanupFailureCode;
    }

    private static async Task<bool> StagingObjectExistsAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@table AND c.relkind IN ('r','p'));";
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
