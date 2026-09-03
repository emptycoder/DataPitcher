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

    public Task InitializeAsync(PostgreSqlExecutionContext context, CancellationToken cancellationToken) =>
        _checkpoints.InitializeAsync(context, cancellationToken);

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
        await new PostgreSqlBatchStageWriter().StageAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            cancellationToken
        );
        var result = await new PostgreSqlBatchApplier().ApplyAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            cancellationToken
        );
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
        var checkpoint = (await _checkpoints.ReadAsync(context.JobId, context.RunId, cancellationToken))!;
        await _barrier.WaitAsync(checkpoint, cancellationToken);
        await _mirror.WriteAsync(checkpoint, cancellationToken);
        return new PostgreSqlBatchCommit(batch.Sequence, result.Affected, result.Inserts, result.Updates);
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
