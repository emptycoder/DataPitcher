using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerConnectionProbe : ICapabilityDetector
{
    /// <summary>Generous enough for a large catalog on a small or serverless tier; probes stay read-only.</summary>
    private const int ProbeTimeoutSeconds = 30;

    static SqlServerConnectionProbe() => SqlServerEntraAuthentication.EnsureRegistered();

    public async Task<ConnectionProbeEvidence> ProbeAsync(
        ConnectionProbeRequest request,
        CancellationToken cancellationToken
    )
    {
        var builder = new SqlConnectionStringBuilder(request.ResolvedConnectionString)
        {
            ConnectTimeout = ProbeTimeoutSeconds,
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var scalar = new SqlCommand("SELECT 1;", connection) { CommandTimeout = ProbeTimeoutSeconds };
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
        var notes = new List<string>();
        var permissions = await ReadPermissionsAsync(connection, request.Profile, notes, cancellationToken);
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
        return new ConnectionProbeEvidence(databaseIdentity, providerVersion, available, cleanupFailureCode, notes);
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
            CommandTimeout = ProbeTimeoutSeconds,
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>
    /// Asks for the least privilege that lets DataPitcher do its job. Reading the schema only needs the catalog
    /// views, which every connected principal can query for the objects it can see. Reading or writing rows is
    /// satisfied by a database-wide grant, a grant on the business schema, or a grant on at least one user table:
    /// the tables that matter are only known when a plan is sealed, and that step verifies them exactly. A business
    /// schema that does not exist in this database counts as "no grant" instead of failing the probe.
    /// </summary>
    private static async Task<(
        bool CanReadSchema,
        bool CanReadBusinessRows,
        bool CanCreateStaging,
        bool CanWriteBusinessRows,
        bool CanPreserveIdentity,
        bool CanUseSnapshotIsolation
    )> ReadPermissionsAsync(
        SqlConnection connection,
        ConnectionProfile profile,
        List<string> notes,
        CancellationToken cancellationToken
    )
    {
        var canReadSchema = await CanReadCatalogAsync(connection, notes, cancellationToken);
        var sql =
            "SELECT "
            + AnyGrant("SELECT")
            + ", "
            + "ISNULL(HAS_PERMS_BY_NAME(@stagingSchema, 'SCHEMA', 'ALTER'), 0), "
            + AnyGrant("INSERT")
            + ", "
            + AnyGrant("ALTER")
            + ", "
            + "ISNULL((SELECT snapshot_isolation_state FROM sys.databases WHERE name = DB_NAME()), 0), "
            + "SUSER_SNAME(), "
            + "CASE WHEN SCHEMA_ID(@businessSchema) IS NULL THEN 0 ELSE 1 END, "
            + "ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'SELECT'), 0), "
            + "(SELECT COUNT(*) FROM sys.tables t WHERE HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name), 'OBJECT', 'SELECT') = 1), "
            + "(SELECT COUNT(*) FROM sys.tables t WHERE t.schema_id = SCHEMA_ID(@businessSchema));";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = ProbeTimeoutSeconds };
        command.Parameters.AddWithValue("@businessSchema", profile.BusinessSchema);
        command.Parameters.AddWithValue("@stagingSchema", profile.StagingSchema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var login = reader.IsDBNull(5) ? "(unknown)" : reader.GetString(5);
        var schemaExists = reader.GetInt32(6) != 0;
        var databaseSelect = reader.GetInt32(7) != 0;
        var readableTables = reader.GetInt32(8);
        var schemaTables = reader.GetInt32(9);
        notes.Add($"Connected as '{login}'.");
        notes.Add(
            schemaExists
                ? $"Schema '{profile.BusinessSchema}' exists with {schemaTables} visible table(s)."
                : $"Schema '{profile.BusinessSchema}' does not exist in this database; set the connection's schema to where the tables live."
        );
        notes.Add(
            databaseSelect
                ? "SELECT is granted at database level."
                : $"SELECT is not granted at database level; {readableTables} user table(s) are readable."
        );
        var snapshotIsolation = reader.GetByte(4) != 0;
        if (!snapshotIsolation)
            notes.Add(
                "Snapshot isolation is off for this database (ALLOW_SNAPSHOT_ISOLATION); reads use the default isolation level. Optional."
            );
        return (
            canReadSchema,
            reader.GetInt32(0) != 0,
            reader.GetInt32(1) != 0,
            reader.GetInt32(2) != 0,
            reader.GetInt32(3) != 0,
            snapshotIsolation
        );
    }

    /// <summary>1 when the permission is held on the database, on the business schema, or on any user table.</summary>
    private static string AnyGrant(string permission) =>
        "CASE WHEN ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', '"
        + permission
        + "'), 0) = 1"
        + " OR ISNULL(HAS_PERMS_BY_NAME(@businessSchema, 'SCHEMA', '"
        + permission
        + "'), 0) = 1"
        + " OR EXISTS (SELECT 1 FROM sys.tables t WHERE HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name), 'OBJECT', '"
        + permission
        + "') = 1)"
        + " THEN 1 ELSE 0 END";

    private static async Task<bool> CanReadCatalogAsync(
        SqlConnection connection,
        List<string> notes,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var command = new SqlCommand(
                "SELECT TOP (1) t.object_id FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id JOIN sys.columns c ON c.object_id = t.object_id;",
                connection
            )
            {
                CommandTimeout = ProbeTimeoutSeconds,
            };
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (SqlException exception)
        {
            notes.Add("Catalog views could not be read: " + exception.Message);
            return false;
        }
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
        var sql =
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=@schema AND t.name=@table) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = ProbeTimeoutSeconds };
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = ProbeTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
