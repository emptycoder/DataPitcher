using System.Data;
using System.Globalization;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerStrictExact(string targetConnectionString)
{
    public async Task EnsureAvailableAsync(SqlServerWriteTable table, CancellationToken cancellationToken)
    {
        var blockers = await BlockersAsync(table.Target, cancellationToken);
        if (blockers.Count > 0)
            throw new SqlServerStrictExactBlockedException(blockers[0]);
    }

    /// <summary>Why exact-set verification cannot be promised for a target table; empty when it can.</summary>
    public async Task<IReadOnlyList<string>> BlockersAsync(TableAddress table, CancellationToken cancellationToken)
    {
        var target = await ObjectIdAsync(table, cancellationToken);
        var blockers = new List<string>();
        if (
            await ExistsAsync(
                "SELECT 1 FROM sys.triggers WHERE parent_id=@target AND is_disabled=0 AND is_ms_shipped=0",
                target,
                cancellationToken
            )
        )
            blockers.Add($"StrictExact is blocked by a target trigger on {table.Schema}.{table.Name}.");
        if (
            await ExistsAsync(
                "SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id=@target AND is_disabled=0 AND (delete_referential_action<>0 OR update_referential_action<>0)",
                target,
                cancellationToken
            )
        )
            blockers.Add($"StrictExact is blocked by a target cascading write path into {table.Schema}.{table.Name}.");
        return blockers;
    }

    public async Task RecordPlannedAsync(
        SqlServerExecutionContext context,
        SqlServerWriteTable table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureLedgerAsync(connection, transaction, cancellationToken);
        foreach (var key in keys)
        {
            await using var command = new SqlCommand(
                "INSERT [datapitcher].[transfer_write_manifest] VALUES (@job,@run,@schema,@table,@key)",
                connection,
                transaction
            );
            command.Parameters.AddWithValue("@job", context.JobId);
            command.Parameters.AddWithValue("@run", context.RunId);
            command.Parameters.AddWithValue("@schema", table.Target.Schema);
            command.Parameters.AddWithValue("@table", table.Target.Name);
            command.Parameters.Add("@key", SqlDbType.VarBinary, -1).Value = SqlServerStableKeyCodec.Encode(key, table);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The keys the run recorded as written or confirmed present must equal the planned manifest: nothing written
    /// outside the plan, nothing planned left unaccounted for.
    /// </summary>
    public async Task VerifyAsync(SqlServerExecutionContext context, CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT (SELECT COUNT(*) FROM (SELECT table_schema,table_name,stable_key FROM [datapitcher].[transfer_affected_keys] WHERE job_id=@job AND run_id=@run EXCEPT SELECT table_schema,table_name,stable_key FROM [datapitcher].[transfer_write_manifest] WHERE job_id=@job AND run_id=@run) o), (SELECT COUNT(*) FROM (SELECT table_schema,table_name,stable_key FROM [datapitcher].[transfer_write_manifest] WHERE job_id=@job AND run_id=@run EXCEPT SELECT table_schema,table_name,stable_key FROM [datapitcher].[transfer_affected_keys] WHERE job_id=@job AND run_id=@run) u)";
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@job", context.JobId);
        command.Parameters.AddWithValue("@run", context.RunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var outside = reader.GetInt32(0);
        var unaccounted = reader.GetInt32(1);
        if (outside + unaccounted != 0)
            throw new TransferVerificationException(
                $"Committed affected keys differ from the planned write manifest: {outside} key(s) were written outside the plan and {unaccounted} planned key(s) were neither written nor found in the target."
            );
    }

    /// <summary>The target table's object id, matched without regard to the plan's spelling of its name.</summary>
    private async Task<int> ObjectIdAsync(TableAddress table, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT TOP 1 t.object_id FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE LOWER(s.name)=LOWER(@schema) AND LOWER(t.name)=LOWER(@table) ORDER BY CASE WHEN s.name=@schema AND t.name=@table THEN 0 ELSE 1 END",
            connection
        );
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Name);
        return await command.ExecuteScalarAsync(cancellationToken) is int id
            ? id
            : throw new InvalidOperationException($"Target table {table.Schema}.{table.Name} does not exist.");
    }

    private async Task<bool> ExistsAsync(string sql, int target, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@target", target);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task EnsureLedgerAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        await using var command = new SqlCommand(
            "IF SCHEMA_ID(N'datapitcher') IS NULL EXEC(N'CREATE SCHEMA [datapitcher]'); IF OBJECT_ID(N'[datapitcher].[transfer_affected_keys]',N'U') IS NULL CREATE TABLE [datapitcher].[transfer_affected_keys] (job_id uniqueidentifier NOT NULL,run_id uniqueidentifier NOT NULL,table_schema sysname NOT NULL,table_name sysname NOT NULL,stable_key varbinary(max) NOT NULL,action_name nvarchar(6) NOT NULL); IF OBJECT_ID(N'[datapitcher].[transfer_write_manifest]',N'U') IS NULL CREATE TABLE [datapitcher].[transfer_write_manifest] (job_id uniqueidentifier NOT NULL,run_id uniqueidentifier NOT NULL,table_schema sysname NOT NULL,table_name sysname NOT NULL,stable_key varbinary(max) NOT NULL);",
            connection,
            transaction
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class SqlServerIdentityRealigner(string targetConnectionString)
{
    public async Task RealignAsync(SqlServerWriteTable table, string column, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var target = SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name);
        const string identitySql =
            "SELECT ic.last_value,ic.increment_value,ic.seed_value FROM sys.identity_columns ic WHERE ic.object_id=OBJECT_ID(@target) AND ic.name=@column";
        await using var identity = new SqlCommand(identitySql, connection, transaction);
        identity.Parameters.AddWithValue("@target", target);
        identity.Parameters.AddWithValue("@column", column);
        await using var identityRows = await identity.ExecuteReaderAsync(cancellationToken);
        if (!await identityRows.ReadAsync(cancellationToken))
        {
            await identityRows.CloseAsync();
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        var current = identityRows.IsDBNull(0)
            ? (long?)null
            : Convert.ToInt64(identityRows.GetValue(0), CultureInfo.InvariantCulture);
        var increment = Convert.ToInt64(identityRows.GetValue(1), CultureInfo.InvariantCulture);
        var seed = Convert.ToInt64(identityRows.GetValue(2), CultureInfo.InvariantCulture);
        await identityRows.CloseAsync();
        var extremeSql =
            "SELECT "
            + (increment > 0 ? "MAX" : "MIN")
            + "("
            + SqlServerIdentifier.Quote(column)
            + ") FROM "
            + target
            + " WITH (TABLOCKX,HOLDLOCK)";
        await using var extreme = new SqlCommand(extremeSql, connection, transaction);
        var value = await extreme.ExecuteScalarAsync(cancellationToken);
        if (value is DBNull)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        var bound = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        var position = current ?? checked(seed - increment);
        var safe = increment > 0 ? position >= bound : position <= bound;
        if (safe)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        await using var reseed = new SqlCommand(
            "DBCC CHECKIDENT (N'"
                + target.Replace("'", "''", StringComparison.Ordinal)
                + "', RESEED, "
                + bound.ToString(CultureInfo.InvariantCulture)
                + ")",
            connection,
            transaction
        );
        await reseed.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
