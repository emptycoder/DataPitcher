using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlConnectionProbe : ICapabilityDetector
{
    /// <summary>Generous enough for a large catalog on a small tier; probes stay read-only.</summary>
    private const int ProbeTimeoutSeconds = 30;

    public async Task<ConnectionProbeEvidence> ProbeAsync(
        ConnectionProbeRequest request,
        CancellationToken cancellationToken
    )
    {
        var builder = new NpgsqlConnectionStringBuilder(request.ResolvedConnectionString)
        {
            Timeout = ProbeTimeoutSeconds,
            CommandTimeout = ProbeTimeoutSeconds,
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var scalar = new NpgsqlCommand("SELECT 1;", connection) { CommandTimeout = ProbeTimeoutSeconds };
        _ = await scalar.ExecuteScalarAsync(cancellationToken);
        return await ReadEvidenceAsync(connection, request, cancellationToken);
    }

    private static async Task<ConnectionProbeEvidence> ReadEvidenceAsync(
        NpgsqlConnection connection,
        ConnectionProbeRequest request,
        CancellationToken cancellationToken
    )
    {
        var available = new HashSet<ConnectionCapability>
        {
            ConnectionCapability.CanConnect,
            ConnectionCapability.CanUseTransactions,
            ConnectionCapability.CanUseSnapshotIsolation,
        };
        var (databaseIdentity, providerVersion) = await ReadIdentityAsync(connection, cancellationToken);
        var notes = new List<string>();
        var permissions = await ReadPermissionsAsync(connection, request.Profile, notes, cancellationToken);
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
        if (
            request.Role is ConnectionRole.Source
            && available.Contains(ConnectionCapability.CanCreateSourceStaging)
            && available.Contains(ConnectionCapability.CanDropSourceStaging)
        )
            available.Add(ConnectionCapability.SupportsDurableResume);
        return new ConnectionProbeEvidence(databaseIdentity, providerVersion, available, cleanupFailureCode, notes);
    }

    private static async Task<(string DatabaseIdentity, string ProviderVersion)> ReadIdentityAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using var command = new NpgsqlCommand("SELECT current_database(), version();", connection)
        {
            CommandTimeout = ProbeTimeoutSeconds,
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>
    /// Asks for the least privilege that lets DataPitcher do its job. The catalog is readable by every role, so
    /// reading the schema only needs a working connection. Reading or writing rows is satisfied by a grant on at
    /// least one table (with USAGE on its schema) in the business schema, or failing that in any user schema: the
    /// tables that matter are only known when a plan is sealed, and that step verifies them exactly. A business or
    /// staging schema that does not exist counts as "no grant" instead of failing the probe.
    /// </summary>
    private static async Task<(
        bool CanReadSchema,
        bool CanReadBusinessRows,
        bool CanCreateStaging,
        bool CanWriteBusinessRows
    )> ReadPermissionsAsync(
        NpgsqlConnection connection,
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
            + "COALESCE((SELECT has_schema_privilege(n.oid, 'CREATE') FROM pg_namespace n WHERE n.nspname = @stagingSchema), false), "
            + AnyGrant("INSERT")
            + ", current_user::text"
            + ", EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @businessSchema)"
            + ", (SELECT count(*)::int FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r', 'p') AND has_schema_privilege(n.oid, 'USAGE') AND has_table_privilege(c.oid, 'SELECT') AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%')"
            + ", (SELECT count(*)::int FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r', 'p') AND n.nspname = @businessSchema);";
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = ProbeTimeoutSeconds };
        command.Parameters.AddWithValue("businessSchema", profile.BusinessSchema);
        command.Parameters.AddWithValue("stagingSchema", profile.StagingSchema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var schemaExists = reader.GetBoolean(4);
        notes.Add($"Connected as '{reader.GetString(3)}'.");
        notes.Add(
            schemaExists
                ? $"Schema '{profile.BusinessSchema}' exists with {reader.GetInt32(6)} table(s)."
                : $"Schema '{profile.BusinessSchema}' does not exist in this database; set the connection's schema to where the tables live."
        );
        notes.Add($"{reader.GetInt32(5)} user table(s) are readable.");
        return (canReadSchema, reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
    }

    /// <summary>True when the privilege is held on at least one table of the business schema, else of any user schema.</summary>
    private static string AnyGrant(string privilege) =>
        "COALESCE((SELECT bool_or(has_schema_privilege(n.oid, 'USAGE') AND has_table_privilege(c.oid, '"
        + privilege
        + "')) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r', 'p') AND n.nspname = @businessSchema), false)"
        + " OR COALESCE((SELECT bool_or(has_schema_privilege(n.oid, 'USAGE') AND has_table_privilege(c.oid, '"
        + privilege
        + "')) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'), false)";

    private static async Task<bool> CanReadCatalogAsync(
        NpgsqlConnection connection,
        List<string> notes,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace JOIN pg_attribute a ON a.attrelid = c.oid WHERE c.relkind IN ('r', 'p') LIMIT 1;",
                connection
            )
            {
                CommandTimeout = ProbeTimeoutSeconds,
            };
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (PostgresException exception)
        {
            notes.Add("Catalog could not be read: " + exception.MessageText);
            return false;
        }
    }

    private static async Task<string?> ProbeStagingAsync(
        NpgsqlConnection connection,
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
                "CREATE TABLE "
                    + PostgreSqlIdentifier.Qualified(request.Profile.StagingSchema, name)
                    + " (value integer);",
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
                        "DROP TABLE " + PostgreSqlIdentifier.Qualified(request.Profile.StagingSchema, name) + ";",
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
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken
    )
    {
        var sql =
            "SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@table AND c.relkind IN ('r','p'));";
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = ProbeTimeoutSeconds };
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = ProbeTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
