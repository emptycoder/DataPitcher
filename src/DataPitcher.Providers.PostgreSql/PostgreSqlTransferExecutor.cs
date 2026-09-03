using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlTransferExecutor
{
    private readonly NpgsqlDataSource _target;
    private readonly IDerivedCheckpointMirror _mirror;
    private readonly IAfterTargetCommitBarrier _barrier;
    private readonly PostgreSqlTargetCheckpointStore _checkpoints;

    public PostgreSqlTransferExecutor(
        NpgsqlDataSource target,
        IDerivedCheckpointMirror mirror,
        IAfterTargetCommitBarrier barrier
    )
    {
        _target = target;
        _mirror = mirror;
        _barrier = barrier;
        _checkpoints = new PostgreSqlTargetCheckpointStore(target);
    }

    private readonly PostgreSqlBatchStageWriter _stageWriter = new();
    private readonly PostgreSqlBatchApplier _applier = new();

    // A new fence token re-acquires the checkpoint, so it is part of the key.
    private (Guid JobId, Guid RunId, long FenceToken)? _initialized;

    public async Task InitializeAsync(PostgreSqlExecutionContext context, CancellationToken cancellationToken)
    {
        if (_initialized == (context.JobId, context.RunId, context.FenceToken))
            return;
        await _checkpoints.InitializeAsync(context, cancellationToken);
        _initialized = (context.JobId, context.RunId, context.FenceToken);
    }

    public async Task<PostgreSqlBatchCommit> ExecuteAsync(
        PostgreSqlExecutionContext context,
        PostgreSqlWriteTable table,
        PostgreSqlTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        await InitializeAsync(context, cancellationToken);
        await using var connection = await _target.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
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
            0,
            cancellationToken
        );
        await transaction.CommitAsync(cancellationToken);
        var checkpoint = await CommittedAsync(connection, context, cancellationToken);
        return new PostgreSqlBatchCommit(batch.Sequence, result.Affected, result.Inserts, result.Updates, checkpoint);
    }

    /// <summary>
    /// Second phase: fills in the deferred columns of rows this run wrote. <paramref name="table"/> carries the stable
    /// key plus the deferred columns only. Row counters stay untouched; the checkpoint records phase 1.
    /// </summary>
    public async Task<PostgreSqlBatchCommit> BackfillAsync(
        PostgreSqlExecutionContext context,
        PostgreSqlWriteTable table,
        PostgreSqlTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        await InitializeAsync(context, cancellationToken);
        await using var connection = await _target.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await _applier.BackfillAsync(connection, transaction, context, table, batch, cancellationToken);
        await _checkpoints.AdvanceAsync(connection, transaction, context, table, batch, 0, 0, 0, 1, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var checkpoint = await CommittedAsync(connection, context, cancellationToken);
        return new PostgreSqlBatchCommit(batch.Sequence, affected, 0, 0, checkpoint);
    }

    private async Task<PostgreSqlTargetCheckpoint> CommittedAsync(
        NpgsqlConnection connection,
        PostgreSqlExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var checkpoint = (await _checkpoints.ReadAsync(connection, context.JobId, context.RunId, cancellationToken))!;
        await _barrier.WaitAsync(checkpoint, cancellationToken);
        await _mirror.WriteAsync(checkpoint, cancellationToken);
        return checkpoint;
    }

    public async Task<PostgreSqlResumePoint> RecoverAsync(
        PostgreSqlExecutionContext context,
        PostgreSqlWriteTable table,
        CancellationToken cancellationToken
    )
    {
        var checkpoint =
            await _checkpoints.ReadAsync(context.JobId, context.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Target checkpoint was not initialized.");
        if (!StringComparer.Ordinal.Equals(checkpoint.ManifestHash, context.ManifestHash))
            throw new PostgreSqlManifestMismatchException();
        await _mirror.WriteAsync(checkpoint, cancellationToken);
        return new PostgreSqlResumePoint(
            checkpoint.LastBatchSequence + 1,
            checkpoint.LastBatchSequence < 0 ? null : PostgreSqlStableKeyCodec.Decode(checkpoint.LastStableKey, table)
        );
    }
}
