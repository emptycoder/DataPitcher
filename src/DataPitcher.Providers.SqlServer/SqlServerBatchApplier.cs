using System.Data;
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
    private bool _ledgerEnsured;

    public async Task<SqlServerApplyResult> ApplyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlServerExecutionContext context,
        SqlServerWriteTable table,
        SqlServerTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        if (!_ledgerEnsured)
        {
            await EnsureLedgerAsync(connection, transaction, cancellationToken);
            _ledgerEnsured = true;
        }
        _affected.Clear();
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
            // Rows the target already had are recorded too, so verification can account for every planned key.
            var skipped = batch.Rows.Select(row => row.StableKey).Except(_affected).ToArray();
            await RecordAsync(connection, transaction, context, table, skipped, "SKIP", cancellationToken);
            await RecordPlannedAsync(connection, transaction, context, table, batch, cancellationToken);
            return new SqlServerApplyResult(inserts + updates, inserts, updates);
        }
        finally
        {
            if (table.InsertColumns.Any(column => column.IsIdentity))
                await SetIdentityInsertAsync(connection, transaction, table, false, cancellationToken);
        }
    }

    /// <summary>
    /// Fills in the deferred columns of rows this run wrote: the batch carries stable keys plus the deferred values,
    /// and only keys in the run's ledger are touched so rows the target already had stay as they were.
    /// </summary>
    public async Task<long> BackfillAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlServerExecutionContext context,
        SqlServerWriteTable table,
        SqlServerTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        if (!_ledgerEnsured)
        {
            await EnsureLedgerAsync(connection, transaction, cancellationToken);
            _ledgerEnsured = true;
        }
        var deferred = table.InsertColumns.Where(column => !column.IsStableKey).ToArray();
        var declarations = string.Join(
            ",",
            table
                .StableKeyColumns.Select(column =>
                    SqlServerIdentifier.Quote(column.Name)
                    + " "
                    + column.StoreType
                    + (column.Collation is null ? "" : " COLLATE " + column.Collation)
                    + " NOT NULL"
                )
                .Concat(
                    deferred.Select(column => SqlServerIdentifier.Quote(column.Name) + " " + column.StoreType + " NULL")
                )
                .Append("[__stable_key] varbinary(max) NOT NULL")
        );
        await using (
            var create = new SqlCommand("CREATE TABLE #backfill (" + declarations + ");", connection, transaction)
        )
            await create.ExecuteNonQueryAsync(cancellationToken);
        var data = new DataTable();
        foreach (var column in table.InsertColumns)
            data.Columns.Add(column.Name, column.ClrType);
        data.Columns.Add("__stable_key", typeof(byte[]));
        foreach (var row in batch.Rows)
        {
            var values = data.NewRow();
            foreach (var column in table.InsertColumns)
                values[column.Name] = row.Values[column.Name] ?? DBNull.Value;
            values["__stable_key"] = SqlServerStableKeyCodec.Encode(row.StableKey, table);
            data.Rows.Add(values);
        }
        using (var reader = data.CreateDataReader())
        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
        {
            bulk.DestinationTableName = "#backfill";
            bulk.BatchSize = 0;
            bulk.BulkCopyTimeout = 30;
            bulk.EnableStreaming = true;
            for (var ordinal = 0; ordinal < data.Columns.Count; ordinal++)
                bulk.ColumnMappings.Add(ordinal, data.Columns[ordinal].ColumnName);
            await bulk.WriteToServerAsync(reader, cancellationToken);
        }
        var set = string.Join(
            ",",
            deferred.Select(column =>
                "t." + SqlServerIdentifier.Quote(column.Name) + "=b." + SqlServerIdentifier.Quote(column.Name)
            )
        );
        await using var update = new SqlCommand(
            "UPDATE t SET "
                + set
                + " FROM "
                + SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name)
                + " t JOIN #backfill b ON "
                + Join(table.StableKeyColumns, "b", "t")
                + " WHERE EXISTS (SELECT 1 FROM [datapitcher].[transfer_affected_keys] l WHERE l.job_id=@job AND l.run_id=@run AND l.table_schema=@schema AND l.table_name=@name AND l.action_name<>'SKIP' AND l.stable_key=b.[__stable_key]); DROP TABLE #backfill;",
            connection,
            transaction
        );
        update.Parameters.AddWithValue("@job", context.JobId);
        update.Parameters.AddWithValue("@run", context.RunId);
        update.Parameters.AddWithValue("@schema", table.Target.Schema);
        update.Parameters.AddWithValue("@name", table.Target.Name);
        return await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private readonly HashSet<StableKey> _affected = [];

    private async Task<long> ApplyAndRecordAsync(
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
        await RecordAsync(connection, transaction, context, table, keys, action, cancellationToken);
        _affected.UnionWith(keys);
        return keys.Count;
    }

    /// <summary>Every row of the batch is a planned row: the manifest the run is verified against grows with it.</summary>
    private static async Task RecordPlannedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlServerExecutionContext context,
        SqlServerWriteTable table,
        SqlServerTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        var data = new DataTable();
        data.Columns.Add("job_id", typeof(Guid));
        data.Columns.Add("run_id", typeof(Guid));
        data.Columns.Add("table_schema", typeof(string));
        data.Columns.Add("table_name", typeof(string));
        data.Columns.Add("stable_key", typeof(byte[]));
        foreach (var row in batch.Rows)
            data.Rows.Add(
                context.JobId,
                context.RunId,
                table.Target.Schema,
                table.Target.Name,
                SqlServerStableKeyCodec.Encode(row.StableKey, table)
            );
        using var reader = data.CreateDataReader();
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = "[datapitcher].[transfer_write_manifest]",
            BatchSize = 0,
            BulkCopyTimeout = 30,
            EnableStreaming = true,
        };
        for (var ordinal = 0; ordinal < data.Columns.Count; ordinal++)
            bulk.ColumnMappings.Add(ordinal, data.Columns[ordinal].ColumnName);
        await bulk.WriteToServerAsync(reader, cancellationToken);
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
        // A row the target already has, by stable key or by any other unique key, is skipped rather than failing the
        // batch on a duplicate-key error. SQL Server unique indexes treat NULL as one value, so NULLs compare equal.
        var predicate =
            policy == SqlServerConflictPolicy.InsertOnly
                ? ""
                : string.Concat(
                    new[] { table.StableKeyColumns }
                        .Concat(table.UniqueKeys)
                        .Select(key =>
                            " AND NOT EXISTS (SELECT 1 FROM "
                            + target
                            + " t WITH (UPDLOCK,HOLDLOCK) WHERE "
                            + string.Join(
                                " AND ",
                                key.Select(column =>
                                    "(s."
                                    + SqlServerIdentifier.Quote(column.Name)
                                    + "=t."
                                    + SqlServerIdentifier.Quote(column.Name)
                                    + " OR (s."
                                    + SqlServerIdentifier.Quote(column.Name)
                                    + " IS NULL AND t."
                                    + SqlServerIdentifier.Quote(column.Name)
                                    + " IS NULL))"
                                )
                            )
                            + ")"
                        )
                );
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

    /// <summary>Writes every affected key of the batch to the ledger in one bulk copy instead of one INSERT per key.</summary>
    private static async Task RecordAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlServerExecutionContext context,
        SqlServerWriteTable table,
        IReadOnlyList<StableKey> keys,
        string action,
        CancellationToken cancellationToken
    )
    {
        if (keys.Count == 0)
            return;
        var data = new DataTable();
        data.Columns.Add("job_id", typeof(Guid));
        data.Columns.Add("run_id", typeof(Guid));
        data.Columns.Add("table_schema", typeof(string));
        data.Columns.Add("table_name", typeof(string));
        data.Columns.Add("stable_key", typeof(byte[]));
        data.Columns.Add("action_name", typeof(string));
        foreach (var key in keys)
            data.Rows.Add(
                context.JobId,
                context.RunId,
                table.Target.Schema,
                table.Target.Name,
                SqlServerStableKeyCodec.Encode(key, table),
                action
            );
        using var reader = data.CreateDataReader();
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = "[datapitcher].[transfer_affected_keys]",
            BatchSize = 0,
            BulkCopyTimeout = 30,
            EnableStreaming = true,
        };
        for (var ordinal = 0; ordinal < data.Columns.Count; ordinal++)
            bulk.ColumnMappings.Add(ordinal, data.Columns[ordinal].ColumnName);
        await bulk.WriteToServerAsync(reader, cancellationToken);
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
