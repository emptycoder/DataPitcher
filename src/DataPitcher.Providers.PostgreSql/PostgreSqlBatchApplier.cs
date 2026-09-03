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
    public async Task<PostgreSqlApplyResult> ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlExecutionContext context,
        PostgreSqlWriteTable table,
        PostgreSqlTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        await EnsureLedgerAsync(connection, transaction, cancellationToken);
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
        foreach (var key in affected)
            await RecordAsync(connection, transaction, context, table, key, cancellationToken);
        return new PostgreSqlApplyResult(affected.Count, inserts.Count, updates.Count);
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

    private static async Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlExecutionContext context,
        PostgreSqlWriteTable table,
        StableKey key,
        CancellationToken cancellationToken
    )
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO datapitcher.transfer_affected_keys VALUES (@job,@run,@schema,@table,@key) ON CONFLICT DO NOTHING",
            connection,
            transaction
        );
        command.Parameters.AddWithValue("job", context.JobId);
        command.Parameters.AddWithValue("run", context.RunId);
        command.Parameters.AddWithValue("schema", table.Target.Schema);
        command.Parameters.AddWithValue("table", table.Target.Name);
        command.Parameters.AddWithValue("key", PostgreSqlStableKeyCodec.Encode(key, table));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
