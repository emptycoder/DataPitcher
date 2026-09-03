using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerTransferExecutor
{
    private readonly string _targetConnectionString;
    private readonly ISqlServerDerivedCheckpointMirror _mirror;
    private readonly ISqlServerAfterTargetCommitBarrier _barrier;
    private readonly SqlServerTargetCheckpointStore _checkpoints;
    private readonly SqlServerBatchStageWriter _stageWriter = new();
    private readonly SqlServerBatchApplier _applier = new();

    // A new fence token re-acquires the checkpoint, so it is part of the key.
    private (Guid JobId, Guid RunId, long FenceToken)? _initialized;

    public SqlServerTransferExecutor(
        string targetConnectionString,
        ISqlServerDerivedCheckpointMirror mirror,
        ISqlServerAfterTargetCommitBarrier barrier
    )
    {
        _targetConnectionString = targetConnectionString;
        _mirror = mirror;
        _barrier = barrier;
        _checkpoints = new SqlServerTargetCheckpointStore(targetConnectionString);
    }

    public async Task InitializeAsync(SqlServerExecutionContext context, CancellationToken cancellationToken)
    {
        if (_initialized == (context.JobId, context.RunId, context.FenceToken))
            return;
        await _checkpoints.InitializeAsync(context, cancellationToken);
        _initialized = (context.JobId, context.RunId, context.FenceToken);
    }

    public async Task<SqlServerBatchCommit> ExecuteAsync(
        SqlServerExecutionContext context,
        SqlServerWriteTable table,
        SqlServerTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        await InitializeAsync(context, cancellationToken);
        await using var connection = new SqlConnection(_targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await _stageWriter.StageAsync(connection, transaction, context, table, batch, cancellationToken);
        var result = await _applier.ApplyAsync(connection, transaction, context, table, batch, cancellationToken);
        await _checkpoints.AdvanceAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            result.Affected,
            result.Inserts,
            result.Updates,
            cancellationToken
        );
        await transaction.CommitAsync(cancellationToken);
        var checkpoint =
            await _checkpoints.ReadAsync(connection, context.JobId, context.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Committed target checkpoint was not found.");
        await _barrier.WaitAsync(checkpoint, cancellationToken);
        await _mirror.WriteAsync(checkpoint, cancellationToken);
        return new SqlServerBatchCommit(batch.Sequence, result.Affected, result.Inserts, result.Updates, checkpoint);
    }

    public async Task<SqlServerResumePoint> RecoverAsync(
        SqlServerExecutionContext context,
        SqlServerWriteTable table,
        CancellationToken cancellationToken
    )
    {
        var checkpoint =
            await _checkpoints.ReadAsync(context.JobId, context.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Target checkpoint was not initialized.");
        if (!StringComparer.Ordinal.Equals(checkpoint.ManifestHash, context.ManifestHash))
            throw new SqlServerManifestMismatchException();
        await _mirror.WriteAsync(checkpoint, cancellationToken);
        return new SqlServerResumePoint(
            checkpoint.LastBatchSequence + 1,
            checkpoint.LastBatchSequence < 0 ? null : SqlServerStableKeyCodec.Decode(checkpoint.LastStableKey, table)
        );
    }
}
