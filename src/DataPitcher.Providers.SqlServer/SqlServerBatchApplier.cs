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

public sealed record SqlServerApplyResult(long Affected, long Inserts, long Updates);

public sealed class SqlServerBatchApplier
{
    public async Task<SqlServerApplyResult> ApplyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlServerExecutionContext context,
        SqlServerWriteTable table,
        SqlServerTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        await EnsureLedgerAsync(connection, transaction, cancellationToken);
        var updates =
            batch.Policy == SqlServerConflictPolicy.Upsert && table.UpdateColumns.Count != 0
                ? await ApplyAndRecordAsync(
                    connection,
                    transaction,
                    UpdateSql(table),
                    "#updated",
                    "UPDATE",
                    context,
                    table,
                    batch.Sequence,
                    cancellationToken
                )
                : 0;
        if (table.InsertColumns.Any(column => column.IsIdentity))
            await SetIdentityInsertAsync(connection, transaction, table, true, cancellationToken);
        try
        {
            var inserts = await ApplyAndRecordAsync(
                connection,
                transaction,
                InsertSql(table, batch.Policy),
                "#inserted",
                "INSERT",
                context,
                table,
                batch.Sequence,
                cancellationToken
            );
            return new SqlServerApplyResult(inserts + updates, inserts, updates);
        }
        finally
        {
            if (table.InsertColumns.Any(column => column.IsIdentity))
                await SetIdentityInsertAsync(connection, transaction, table, false, cancellationToken);
        }
    }

    private static async Task<long> ApplyAndRecordAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        string capture,
        string action,
        SqlServerExecutionContext context,
        SqlServerWriteTable table,
        long sequence,
        CancellationToken cancellationToken
    )
    {
        var declarations = string.Join(
            ",",
            table.StableKeyColumns.Select((column, index) => "[k" + index + "] " + column.StoreType + " NOT NULL")
        );
        await using (
            var create = new SqlCommand("CREATE TABLE " + capture + " (" + declarations + ");", connection, transaction)
        )
            await create.ExecuteNonQueryAsync(cancellationToken);
        await using (
            var apply = new SqlCommand(
                sql.Replace("{capture}", capture, StringComparison.Ordinal),
                connection,
                transaction
            )
        )
        {
            AddBatch(apply, context, sequence);
            await apply.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var read = new SqlCommand(
            "SELECT "
                + string.Join(",", table.StableKeyColumns.Select((_, index) => "[k" + index + "]"))
                + " FROM "
                + capture,
            connection,
            transaction
        );
        await using var rows = await read.ExecuteReaderAsync(cancellationToken);
        var keys = new List<StableKey>();
        while (await rows.ReadAsync(cancellationToken))
            keys.Add(
                new StableKey(
                    table.StableKeyColumns.Select(
                        (column, index) => new KeyComponent(column.Name, rows.GetValue(index))
                    )
                )
            );
        await rows.CloseAsync();
        foreach (var key in keys)
            await RecordAsync(connection, transaction, context, table, key, action, cancellationToken);
        return keys.Count;
    }

    private static string UpdateSql(SqlServerWriteTable table)
    {
        var target = SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name);
        var stage = SqlServerBatchStageWriter.StageName(table);
        var set = string.Join(
            ",",
            table.UpdateColumns.Select(column =>
                "t." + SqlServerIdentifier.Quote(column.Name) + "=s." + SqlServerIdentifier.Quote(column.Name)
            )
        );
        var output = string.Join(
            ",",
            table.StableKeyColumns.Select(column => "INSERTED." + SqlServerIdentifier.Quote(column.Name))
        );
        var join = Join(table.StableKeyColumns, "s", "t");
        return "UPDATE t SET "
            + set
            + " OUTPUT "
            + output
            + " INTO {capture} FROM "
            + target
            + " t JOIN "
            + stage
            + " s ON "
            + join
            + " WHERE s.job_id=@job AND s.run_id=@run AND s.fence_token=@fence AND s.batch_sequence=@sequence";
    }

    private static string InsertSql(SqlServerWriteTable table, SqlServerConflictPolicy policy)
    {
        var target = SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name);
        var stage = SqlServerBatchStageWriter.StageName(table);
        var columns = string.Join(",", table.InsertColumns.Select(column => SqlServerIdentifier.Quote(column.Name)));
        var output = string.Join(
            ",",
            table.StableKeyColumns.Select(column => "INSERTED." + SqlServerIdentifier.Quote(column.Name))
        );
        var predicate =
            policy == SqlServerConflictPolicy.InsertOnly
                ? ""
                : " AND NOT EXISTS (SELECT 1 FROM "
                    + target
                    + " t WITH (UPDLOCK,HOLDLOCK) WHERE "
                    + Join(table.StableKeyColumns, "s", "t")
                    + ")";
        return "INSERT "
            + target
            + " ("
            + columns
            + ") OUTPUT "
            + output
            + " INTO {capture} SELECT "
            + columns
            + " FROM "
            + stage
            + " s WHERE s.job_id=@job AND s.run_id=@run AND s.fence_token=@fence AND s.batch_sequence=@sequence"
            + predicate;
    }

    private static string Join(IEnumerable<SqlServerWriteColumn> columns, string left, string right) =>
        string.Join(
            " AND ",
            columns.Select(column =>
                left
                + "."
                + SqlServerIdentifier.Quote(column.Name)
                + "="
                + right
                + "."
                + SqlServerIdentifier.Quote(column.Name)
            )
        );

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

    private static async Task RecordAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlServerExecutionContext context,
        SqlServerWriteTable table,
        StableKey key,
        string action,
        CancellationToken cancellationToken
    )
    {
        await using var command = new SqlCommand(
            "INSERT [datapitcher].[transfer_affected_keys] VALUES (@job,@run,@schema,@table,@key,@action)",
            connection,
            transaction
        );
        command.Parameters.AddWithValue("@job", context.JobId);
        command.Parameters.AddWithValue("@run", context.RunId);
        command.Parameters.AddWithValue("@schema", table.Target.Schema);
        command.Parameters.AddWithValue("@table", table.Target.Name);
        command.Parameters.Add("@key", System.Data.SqlDbType.VarBinary, -1).Value = SqlServerStableKeyCodec.Encode(
            key,
            table
        );
        command.Parameters.AddWithValue("@action", action);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetIdentityInsertAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlServerWriteTable table,
        bool enabled,
        CancellationToken cancellationToken
    )
    {
        await using var command = new SqlCommand(
            "SET IDENTITY_INSERT "
                + SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name)
                + (enabled ? " ON" : " OFF"),
            connection,
            transaction
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddBatch(SqlCommand command, SqlServerExecutionContext context, long sequence)
    {
        command.Parameters.AddWithValue("@job", context.JobId);
        command.Parameters.AddWithValue("@run", context.RunId);
        command.Parameters.AddWithValue("@fence", context.FenceToken);
        command.Parameters.AddWithValue("@sequence", sequence);
    }
}
