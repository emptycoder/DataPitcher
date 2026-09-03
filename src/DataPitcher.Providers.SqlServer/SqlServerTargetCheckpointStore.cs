using System.Data;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerTargetCheckpointStore(string targetConnectionString)
{
    private const string Name = "[datapitcher].[transfer_checkpoints]";

    public async Task InitializeAsync(SqlServerExecutionContext context, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await EnsureAsync(connection, transaction, cancellationToken);
        var existing = await ReadAsync(connection, transaction, context.JobId, context.RunId, cancellationToken);
        if (existing is null)
            await ExecuteAsync(connection, transaction, "INSERT " + Name + " (job_id,run_id,last_batch_sequence,last_stable_key,cumulative_affected,cumulative_inserts,cumulative_updates,manifest_hash,fence_token) VALUES (@job,@run,-1,0x,0,0,0,@hash,@fence)", context, cancellationToken);
        else if (!StringComparer.Ordinal.Equals(existing.ManifestHash, context.ManifestHash)) throw new SqlServerManifestMismatchException();
        else if (existing.FenceToken > context.FenceToken) throw new SqlServerFenceLostException();
        else if (existing.FenceToken < context.FenceToken)
        {
            await ExecuteAsync(connection, transaction, "UPDATE " + Name + " SET fence_token=@fence WHERE job_id=@job AND run_id=@run AND manifest_hash=@hash AND fence_token<@fence", context, cancellationToken);
            var advanced = await ReadAsync(connection, transaction, context.JobId, context.RunId, cancellationToken);
            if (advanced is null || advanced.FenceToken != context.FenceToken) throw new SqlServerFenceLostException();
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SqlServerTargetCheckpoint?> ReadAsync(Guid jobId, Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadAsync(connection, null, jobId, runId, cancellationToken);
    }

    public async Task AdvanceAsync(SqlConnection connection, SqlTransaction transaction, SqlServerExecutionContext context, SqlServerWriteTable table, SqlServerTransferBatch batch, long affected, long inserts, long updates, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE [datapitcher].[transfer_checkpoints] SET last_batch_sequence=@sequence,last_stable_key=@key,cumulative_affected=cumulative_affected+@affected,cumulative_inserts=cumulative_inserts+@inserts,cumulative_updates=cumulative_updates+@updates WHERE job_id=@job AND run_id=@run AND manifest_hash=@hash AND fence_token=@fence AND last_batch_sequence=@previous";
        await using var command = new SqlCommand(sql, connection, transaction);
        AddContext(command, context);
        command.Parameters.Add("@sequence", SqlDbType.BigInt).Value = batch.Sequence;
        command.Parameters.Add("@key", SqlDbType.VarBinary, -1).Value = SqlServerStableKeyCodec.Encode(batch.LastStableKey, table);
        command.Parameters.Add("@affected", SqlDbType.BigInt).Value = affected;
        command.Parameters.Add("@inserts", SqlDbType.BigInt).Value = inserts;
        command.Parameters.Add("@updates", SqlDbType.BigInt).Value = updates;
        command.Parameters.Add("@previous", SqlDbType.BigInt).Value = batch.Sequence - 1;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new SqlServerFenceLostException();
    }

    private static async Task EnsureAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("IF SCHEMA_ID(N'datapitcher') IS NULL EXEC(N'CREATE SCHEMA [datapitcher]'); IF OBJECT_ID(N'[datapitcher].[transfer_checkpoints]',N'U') IS NULL CREATE TABLE [datapitcher].[transfer_checkpoints] (job_id uniqueidentifier NOT NULL,run_id uniqueidentifier NOT NULL,last_batch_sequence bigint NOT NULL,last_stable_key varbinary(max) NOT NULL,last_table nvarchar(512) NULL,cumulative_affected bigint NOT NULL,cumulative_inserts bigint NOT NULL,cumulative_updates bigint NOT NULL,manifest_hash nvarchar(128) NOT NULL,fence_token bigint NOT NULL,PRIMARY KEY(job_id,run_id)); IF COL_LENGTH(N'datapitcher.transfer_checkpoints', N'last_table') IS NULL ALTER TABLE [datapitcher].[transfer_checkpoints] ADD last_table nvarchar(512) NULL;", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SqlServerTargetCheckpoint?> ReadAsync(SqlConnection connection, SqlTransaction? transaction, Guid jobId, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT job_id,run_id,last_batch_sequence,last_stable_key,cumulative_affected,cumulative_inserts,cumulative_updates,manifest_hash,fence_token FROM " + Name + " WHERE job_id=@job AND run_id=@run", connection, transaction);
        command.Parameters.Add("@job", SqlDbType.UniqueIdentifier).Value = jobId;
        command.Parameters.Add("@run", SqlDbType.UniqueIdentifier).Value = runId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SqlServerTargetCheckpoint(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetFieldValue<byte[]>(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetInt64(8))
            : null;
    }

    private static async Task<int> ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, SqlServerExecutionContext context, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        AddContext(command, context);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddContext(SqlCommand command, SqlServerExecutionContext context)
    {
        command.Parameters.Add("@job", SqlDbType.UniqueIdentifier).Value = context.JobId;
        command.Parameters.Add("@run", SqlDbType.UniqueIdentifier).Value = context.RunId;
        command.Parameters.Add("@hash", SqlDbType.NVarChar, 128).Value = context.ManifestHash;
        command.Parameters.Add("@fence", SqlDbType.BigInt).Value = context.FenceToken;
    }
}
