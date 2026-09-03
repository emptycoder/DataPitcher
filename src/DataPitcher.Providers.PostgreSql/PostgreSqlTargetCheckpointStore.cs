using System.Text.Json;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlTargetCheckpointStore(NpgsqlDataSource dataSource)
{
    private const string Name = "datapitcher.transfer_checkpoints";

    public async Task InitializeAsync(PostgreSqlExecutionContext context, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "CREATE SCHEMA IF NOT EXISTS datapitcher; CREATE TABLE IF NOT EXISTS "
                + Name
                + " (job_id uuid NOT NULL, run_id uuid NOT NULL, last_batch_sequence bigint NOT NULL, last_stable_key bytea NOT NULL, last_table text NULL, cumulative_affected bigint NOT NULL, cumulative_inserts bigint NOT NULL, cumulative_updates bigint NOT NULL, manifest_hash text NOT NULL, fence_token bigint NOT NULL, PRIMARY KEY (job_id, run_id)); ALTER TABLE "
                + Name
                + " ADD COLUMN IF NOT EXISTS last_table text NULL",
            cancellationToken
        );
        var existing = await ReadAsync(connection, transaction, context.JobId, context.RunId, cancellationToken);
        if (existing is null)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO "
                    + Name
                    + " (job_id,run_id,last_batch_sequence,last_stable_key,cumulative_affected,cumulative_inserts,cumulative_updates,manifest_hash,fence_token) VALUES (@job,@run,-1,''::bytea,0,0,0,@hash,@fence)",
                cancellationToken,
                context
            );
        }
        else if (!StringComparer.Ordinal.Equals(existing.ManifestHash, context.ManifestHash))
            throw new PostgreSqlManifestMismatchException();
        else if (existing.FenceToken > context.FenceToken)
            throw new PostgreSqlFenceLostException();
        else if (
            existing.FenceToken < context.FenceToken
            && await ExecuteAsync(
                connection,
                transaction,
                "UPDATE " + Name + " SET fence_token=@fence WHERE job_id=@job AND run_id=@run AND fence_token < @fence",
                cancellationToken,
                context
            ) != 1
        )
            throw new PostgreSqlFenceLostException();
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PostgreSqlTargetCheckpoint?> ReadAsync(
        Guid jobId,
        Guid runId,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, jobId, runId, cancellationToken);
    }

    /// <summary>Reads the checkpoint over an already open connection, saving a round trip after a commit.</summary>
    public Task<PostgreSqlTargetCheckpoint?> ReadAsync(
        NpgsqlConnection connection,
        Guid jobId,
        Guid runId,
        CancellationToken cancellationToken
    ) => ReadAsync(connection, null, jobId, runId, cancellationToken);

    public async Task AdvanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlExecutionContext context,
        PostgreSqlWriteTable table,
        PostgreSqlTransferBatch batch,
        long affected,
        long inserts,
        long updates,
        CancellationToken cancellationToken
    )
    {
        var key = PostgreSqlStableKeyCodec.Encode(batch.LastStableKey, table);
        await using var command = new NpgsqlCommand(
            "UPDATE "
                + Name
                + " SET last_batch_sequence=@sequence,last_stable_key=@key,last_table=@table,cumulative_affected=cumulative_affected+@affected,cumulative_inserts=cumulative_inserts+@inserts,cumulative_updates=cumulative_updates+@updates WHERE job_id=@job AND run_id=@run AND manifest_hash=@hash AND fence_token=@fence AND last_batch_sequence=@previous",
            connection,
            transaction
        );
        command.Parameters.AddWithValue("sequence", batch.Sequence);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("table", JsonSerializer.Serialize(table.Target));
        command.Parameters.AddWithValue("affected", affected);
        command.Parameters.AddWithValue("inserts", inserts);
        command.Parameters.AddWithValue("updates", updates);
        AddContext(command, context);
        command.Parameters.AddWithValue("previous", batch.Sequence - 1);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new PostgreSqlFenceLostException();
    }

    private static async Task<PostgreSqlTargetCheckpoint?> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid job,
        Guid run,
        CancellationToken cancellationToken
    )
    {
        await using var command = new NpgsqlCommand(
            "SELECT job_id,run_id,last_batch_sequence,last_stable_key,cumulative_affected,cumulative_inserts,cumulative_updates,manifest_hash,fence_token,last_table FROM "
                + Name
                + " WHERE job_id=@job AND run_id=@run",
            connection,
            transaction
        );
        command.Parameters.AddWithValue("job", job);
        command.Parameters.AddWithValue("run", run);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                reader.GetFieldValue<byte[]>(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetInt64(8),
                reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<TableAddress>(reader.GetString(9))
            )
            : null;
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        PostgreSqlExecutionContext? context = null
    )
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (context is not null)
            AddContext(command, context);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddContext(NpgsqlCommand command, PostgreSqlExecutionContext context)
    {
        command.Parameters.AddWithValue("job", context.JobId);
        command.Parameters.AddWithValue("run", context.RunId);
        command.Parameters.AddWithValue("hash", context.ManifestHash);
        command.Parameters.AddWithValue("fence", context.FenceToken);
    }
}
