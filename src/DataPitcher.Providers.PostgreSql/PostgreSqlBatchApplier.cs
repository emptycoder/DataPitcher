using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed record PostgreSqlApplyResult(long Affected, long Inserts, long Updates);

public sealed class PostgreSqlBatchApplier
{
    private bool _ledgerEnsured;

    public async Task<PostgreSqlApplyResult> ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlExecutionContext context,
        PostgreSqlWriteTable table,
        PostgreSqlTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        if (!_ledgerEnsured)
        {
            await EnsureLedgerAsync(connection, transaction, cancellationToken);
            _ledgerEnsured = true;
        }
        var affected = new List<StableKey>();
        var updates =
            batch.Policy == PostgreSqlConflictPolicy.Upsert
                ? await ExecuteReturningAsync(
                    connection,
                    transaction,
                    UpdateSql(table),
                    context,
                    batch.Sequence,
                    table,
                    cancellationToken
                )
                : [];
        affected.AddRange(updates);
        var inserts = await ExecuteReturningAsync(
            connection,
            transaction,
            InsertSql(table, batch.Policy),
            context,
            batch.Sequence,
            table,
            cancellationToken
        );
        affected.AddRange(inserts);
        await RecordAsync(connection, transaction, context, table, affected, cancellationToken);
        return new PostgreSqlApplyResult(affected.Count, inserts.Count, updates.Count);
    }

    /// <summary>
    /// Fills in the deferred columns of rows this run wrote: the batch carries stable keys plus the deferred values,
    /// and only keys in the run's ledger are touched so rows the target already had stay as they were.
    /// </summary>
    public async Task<long> BackfillAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlExecutionContext context,
        PostgreSqlWriteTable table,
        PostgreSqlTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        if (!_ledgerEnsured)
        {
            await EnsureLedgerAsync(connection, transaction, cancellationToken);
            _ledgerEnsured = true;
        }
        var deferred = table.InsertColumns.Where(column => !column.IsStableKey).ToArray();
        if (deferred.Length == 0)
            return 0;
        const string temp = "datapitcher_backfill";
        var declaration = string.Join(
            ", ",
            table
                .InsertColumns.Select(column =>
                    PostgreSqlIdentifier.Quote(column.Name)
                    + " "
                    + column.StoreType
                    + (column.IsStableKey ? " NOT NULL" : " NULL")
                )
                .Append("__stable_key bytea NOT NULL")
        );
        await using (
            var create = new NpgsqlCommand(
                "CREATE TEMP TABLE " + temp + " (" + declaration + ") ON COMMIT DROP",
                connection,
                transaction
            )
        )
            await create.ExecuteNonQueryAsync(cancellationToken);
        var names = string.Join(
            ", ",
            table.InsertColumns.Select(column => PostgreSqlIdentifier.Quote(column.Name)).Append("__stable_key")
        );
        await using (
            var importer = await connection.BeginBinaryImportAsync(
                "COPY " + temp + " (" + names + ") FROM STDIN (FORMAT BINARY)",
                cancellationToken
            )
        )
        {
            foreach (var row in batch.Rows)
            {
                await importer.StartRowAsync(cancellationToken);
                foreach (var column in table.InsertColumns)
                {
                    var value = row.Values[column.Name];
                    if (value is null)
                        await importer.WriteNullAsync(cancellationToken);
                    else
                        await importer.WriteAsync(value, column.ProviderType, cancellationToken);
                }
                await importer.WriteAsync(
                    PostgreSqlStableKeyCodec.Encode(row.StableKey, table),
                    NpgsqlTypes.NpgsqlDbType.Bytea,
                    cancellationToken
                );
            }
            await importer.CompleteAsync(cancellationToken);
        }
        var set = string.Join(
            ", ",
            deferred.Select(column =>
                PostgreSqlIdentifier.Quote(column.Name) + "=b." + PostgreSqlIdentifier.Quote(column.Name)
            )
        );
        await using var update = new NpgsqlCommand(
            "UPDATE "
                + PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name)
                + " t SET "
                + set
                + " FROM "
                + temp
                + " b WHERE "
                + Join(table.StableKeyColumns, "b", "t")
                + " AND EXISTS (SELECT 1 FROM datapitcher.transfer_affected_keys l WHERE l.job_id=@job AND l.run_id=@run AND l.table_schema=@schema AND l.table_name=@table AND l.stable_key=b.__stable_key)",
            connection,
            transaction
        );
        update.Parameters.AddWithValue("job", context.JobId);
        update.Parameters.AddWithValue("run", context.RunId);
        update.Parameters.AddWithValue("schema", table.Target.Schema);
        update.Parameters.AddWithValue("table", table.Target.Name);
        return await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string InsertSql(PostgreSqlWriteTable table, PostgreSqlConflictPolicy policy)
    {
        var target = PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name);
        var stage = PostgreSqlBatchStageWriter.StageName(table);
        var columns = string.Join(", ", table.InsertColumns.Select(x => PostgreSqlIdentifier.Quote(x.Name)));
        var keys = Join(table.StableKeyColumns, "s", "t");
        var missing =
            policy == PostgreSqlConflictPolicy.InsertOnly
                ? ""
                : " AND NOT EXISTS (SELECT 1 FROM " + target + " t WHERE " + keys + ")";
        var overriding = table.InsertColumns.Any(x => x.IsIdentityAlways) ? " OVERRIDING SYSTEM VALUE" : "";
        return "INSERT INTO "
            + target
            + " ("
            + columns
            + ")"
            + overriding
            + " SELECT "
            + columns
            + " FROM "
            + stage
            + " s WHERE s.job_id=@job AND s.run_id=@run AND s.fence_token=@fence AND s.batch_sequence=@sequence"
            + missing
            + " RETURNING "
            + string.Join(", ", table.StableKeyColumns.Select(x => PostgreSqlIdentifier.Quote(x.Name)));
    }

    private static string UpdateSql(PostgreSqlWriteTable table)
    {
        var target = PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name);
        var stage = PostgreSqlBatchStageWriter.StageName(table);
        var set = string.Join(
            ", ",
            table.UpdateColumns.Select(x =>
                PostgreSqlIdentifier.Quote(x.Name) + "=s." + PostgreSqlIdentifier.Quote(x.Name)
            )
        );
        return "UPDATE "
            + target
            + " t SET "
            + set
            + " FROM "
            + stage
            + " s WHERE s.job_id=@job AND s.run_id=@run AND s.fence_token=@fence AND s.batch_sequence=@sequence AND "
            + Join(table.StableKeyColumns, "s", "t")
            + " RETURNING "
            + string.Join(", ", table.StableKeyColumns.Select(x => "t." + PostgreSqlIdentifier.Quote(x.Name)));
    }

    private static string Join(IEnumerable<PostgreSqlWriteColumn> columns, string left, string right) =>
        string.Join(
            " AND ",
            columns.Select(x =>
                left + "." + PostgreSqlIdentifier.Quote(x.Name) + "=" + right + "." + PostgreSqlIdentifier.Quote(x.Name)
            )
        );

    private static async Task<List<StableKey>> ExecuteReturningAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        PostgreSqlExecutionContext context,
        long sequence,
        PostgreSqlWriteTable table,
        CancellationToken cancellationToken
    )
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("job", context.JobId);
        command.Parameters.AddWithValue("run", context.RunId);
        command.Parameters.AddWithValue("fence", context.FenceToken);
        command.Parameters.AddWithValue("sequence", sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new List<StableKey>();
        while (await reader.ReadAsync(cancellationToken))
            keys.Add(
                new StableKey(
                    table.StableKeyColumns.Select(
                        (column, index) => new KeyComponent(column.Name, reader.GetValue(index))
                    )
                )
            );
        return keys;
    }

    private static async Task EnsureLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        await using var command = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS datapitcher.transfer_affected_keys (job_id uuid NOT NULL,run_id uuid NOT NULL,table_schema text NOT NULL,table_name text NOT NULL,stable_key bytea NOT NULL,PRIMARY KEY(job_id,run_id,table_schema,table_name,stable_key)); CREATE TABLE IF NOT EXISTS datapitcher.transfer_write_manifest (job_id uuid NOT NULL,run_id uuid NOT NULL,table_schema text NOT NULL,table_name text NOT NULL,stable_key bytea NOT NULL,PRIMARY KEY(job_id,run_id,table_schema,table_name,stable_key));",
            connection,
            transaction
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Writes every affected key of the batch to the ledger in one statement instead of one INSERT per key.</summary>
    private static async Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlExecutionContext context,
        PostgreSqlWriteTable table,
        IReadOnlyList<StableKey> keys,
        CancellationToken cancellationToken
    )
    {
        if (keys.Count == 0)
            return;
        await using var command = new NpgsqlCommand(
            "INSERT INTO datapitcher.transfer_affected_keys (job_id, run_id, table_schema, table_name, stable_key) SELECT @job, @run, @schema, @table, unnest(@keys) ON CONFLICT DO NOTHING",
            connection,
            transaction
        );
        command.Parameters.AddWithValue("job", context.JobId);
        command.Parameters.AddWithValue("run", context.RunId);
        command.Parameters.AddWithValue("schema", table.Target.Schema);
        command.Parameters.AddWithValue("table", table.Target.Name);
        command.Parameters.Add("keys", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea).Value =
            keys.Select(key => PostgreSqlStableKeyCodec.Encode(key, table)).ToArray();
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
