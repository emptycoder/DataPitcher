using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerConnectionProbe : ICapabilityDetector
{
    public async Task<ConnectionProbeEvidence> ProbeAsync(
        ConnectionProbeRequest request,
        CancellationToken cancellationToken
    )
    {
        var builder = new SqlConnectionStringBuilder(request.ResolvedConnectionString) { ConnectTimeout = 5 };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var scalar = new SqlCommand("SELECT 1;", connection) { CommandTimeout = 5 };
        _ = await scalar.ExecuteScalarAsync(cancellationToken);
        return await ReadEvidenceAsync(connection, request, cancellationToken);
    }

    private static async Task<ConnectionProbeEvidence> ReadEvidenceAsync(
        SqlConnection connection,
        ConnectionProbeRequest request,
        CancellationToken cancellationToken
    )
    {
        var available = new HashSet<ConnectionCapability>
        {
            ConnectionCapability.CanConnect,
            ConnectionCapability.CanUseTransactions,
        };
        var (databaseIdentity, providerVersion) = await ReadIdentityAsync(connection, cancellationToken);
        var permissions = await ReadPermissionsAsync(connection, request.Profile, cancellationToken);
        if (permissions.CanReadSchema)
            available.Add(ConnectionCapability.CanReadSchema);
        if (permissions.CanReadBusinessRows)
            available.Add(ConnectionCapability.CanReadBusinessRows);
        if (permissions.CanUseSnapshotIsolation)
            available.Add(ConnectionCapability.CanUseSnapshotIsolation);
        if (request.Role is ConnectionRole.Target && permissions.CanWriteBusinessRows)
            available.Add(ConnectionCapability.CanBulkInsert);
        if (request.Role is ConnectionRole.Target && permissions.CanPreserveIdentity)
            available.Add(ConnectionCapability.CanPreserveIdentity);

        string? cleanupFailureCode = null;
        if (request.Mode is TransferMode.ResumableStaged && permissions.CanCreateStaging)
            cleanupFailureCode = await ProbeStagingAsync(connection, request, available, cancellationToken);
        if (
            request.Role is ConnectionRole.Source
            && available.Contains(ConnectionCapability.CanCreateSourceStaging)
            && available.Contains(ConnectionCapability.CanDropSourceStaging)
        )
            available.Add(ConnectionCapability.SupportsDurableResume);
        return new ConnectionProbeEvidence(databaseIdentity, providerVersion, available, cleanupFailureCode);
    }

    private static async Task<(string DatabaseIdentity, string ProviderVersion)> ReadIdentityAsync(
        SqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using var command = new SqlCommand(
            "SELECT DB_NAME(), CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));",
            connection
        )
        {
            CommandTimeout = 5,
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<(
        bool CanReadSchema,
        bool CanReadBusinessRows,
        bool CanCreateStaging,
        bool CanWriteBusinessRows,
        bool CanPreserveIdentity,
        bool CanUseSnapshotIsolation
    )> ReadPermissionsAsync(SqlConnection connection, ConnectionProfile profile, CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONNECT'), "
            + "HAS_PERMS_BY_NAME(@businessSchema, 'SCHEMA', 'SELECT'), "
            + "HAS_PERMS_BY_NAME(@businessSchema, 'SCHEMA', 'SELECT'), "
            + "HAS_PERMS_BY_NAME(@stagingSchema, 'SCHEMA', 'ALTER'), "
            + "HAS_PERMS_BY_NAME(@businessSchema, 'SCHEMA', 'INSERT'), "
            + "HAS_PERMS_BY_NAME(@businessSchema, 'SCHEMA', 'ALTER'), "
            + "(SELECT snapshot_isolation_state FROM sys.databases WHERE name=DB_NAME());";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("@businessSchema", profile.BusinessSchema);
        command.Parameters.AddWithValue("@stagingSchema", profile.StagingSchema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        _ = reader.GetInt32(0);
        return (
            reader.GetInt32(1) != 0,
            reader.GetInt32(2) != 0,
            reader.GetInt32(3) != 0,
            reader.GetInt32(4) != 0,
            reader.GetInt32(5) != 0,
            reader.GetByte(6) != 0
        );
    }

    private static async Task<string?> ProbeStagingAsync(
        SqlConnection connection,
        ConnectionProbeRequest request,
        ISet<ConnectionCapability> available,
        CancellationToken cancellationToken
    )
    {
        var name = "dp_probe_" + Guid.NewGuid().ToString("N");
        var created = false;
        var verified = false;
        string? cleanupFailureCode = null;
        try
        {
            await ExecuteAsync(
                connection,
                "CREATE TABLE " + SqlServerIdentifier.Qualified(request.Profile.StagingSchema, name) + " (value int);",
                cancellationToken
            );
            created = true;
            verified = await StagingObjectExistsAsync(
                connection,
                request.Profile.StagingSchema,
                name,
                cancellationToken
            );
            if (verified)
                available.Add(
                    request.Role is ConnectionRole.Source
                        ? ConnectionCapability.CanCreateSourceStaging
                        : ConnectionCapability.CanCreateTargetStaging
                );
        }
        finally
        {
            if (created)
            {
                try
                {
                    await ExecuteAsync(
                        connection,
                        "DROP TABLE " + SqlServerIdentifier.Qualified(request.Profile.StagingSchema, name) + ";",
                        cancellationToken
                    );
                    if (verified)
                        available.Add(
                            request.Role is ConnectionRole.Source
                                ? ConnectionCapability.CanDropSourceStaging
                                : ConnectionCapability.CanDropTargetStaging
                        );
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
        SqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=@schema AND t.name=@table) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
