using System.Text;
using DataPitcher.Core.Closure;
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

/// <summary>
/// PostgreSQL transfer sessions for the job worker: reads sealed rows from the source in stable-key order and
/// applies them to the target through the fenced, checkpointed <see cref="PostgreSqlTransferExecutor"/>.
/// </summary>
public sealed class PostgreSqlRunSessions(
    IPlanRepository plans,
    IConnectionProfileRepository profiles,
    ISecretReferenceResolver secrets
) : IRunSessionProvider
{
    public string ProviderId => "postgresql";

    public async Task<ITransferReadSession> OpenKeysetAsync(
        TransferRun run,
        StableKey? startAfter,
        CancellationToken cancellationToken,
        TableAddress? table = null,
        TransferPhase phase = TransferPhase.Rows
    )
    {
        var content = await ContentAsync(run, cancellationToken);
        var source = await ConnectionStringAsync(run.SourceConnectionId, cancellationToken);
        await using var target = NpgsqlDataSource.Create(
            await ConnectionStringAsync(run.TargetConnectionId, cancellationToken)
        );
        var checkpoint =
            await new PostgreSqlTargetCheckpointStore(target).ReadAsync(run.JobId, run.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Target checkpoint was not initialized.");
        return new ReadSession(
            NpgsqlDataSource.Create(source),
            content,
            run.PlanId,
            startAfter,
            table,
            BatchSequence.WorkerFromProvider(checkpoint.LastBatchSequence) + 1,
            phase
        );
    }

    public async Task<ITargetRunSession> OpenAsync(TransferRun run, CancellationToken cancellationToken)
    {
        var content = await ContentAsync(run, cancellationToken);
        return new TargetSession(
            NpgsqlDataSource.Create(await ConnectionStringAsync(run.TargetConnectionId, cancellationToken)),
            content
        );
    }

    private async Task<TransferPlanContent> ContentAsync(TransferRun run, CancellationToken cancellationToken) =>
        await plans.LoadContentAsync(run.PlanId, cancellationToken)
        ?? throw new InvalidOperationException("Sealed plan content was not found.");

    private async Task<string> ConnectionStringAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await secrets.ResolveAsync(
            (await profiles.GetProfileAsync(connectionId, cancellationToken)).SecretReference,
            cancellationToken
        );

    private sealed class ReadSession(
        NpgsqlDataSource dataSource,
        TransferPlanContent content,
        Guid planId,
        StableKey? startAfter,
        TableAddress? startTable,
        long nextSequence,
        TransferPhase startPhase
    ) : ITransferReadSession
    {
        private readonly IReadOnlyList<PlanTable> _tables = Tables(content);
        private readonly IReadOnlyList<PlanTable> _deferred = Deferred(content);
        private readonly Dictionary<(TransferPhase Phase, int Index), PostgreSqlWriteTable> _sources = [];
        private TransferPhase _phase = startPhase;
        private int _index = StartIndex(
            startPhase == TransferPhase.Rows ? Tables(content) : Deferred(content),
            startTable
        );
        private StableKey? _after = startAfter;
        private int? _afterGeneration;
        private long _nextSequence = nextSequence;

        public async Task<TransferUnit?> ReadNextAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var tables = _phase == TransferPhase.Rows ? _tables : _deferred;
                if (_index >= tables.Count)
                {
                    if (_phase == TransferPhase.DeferredColumns)
                        return null;
                    // Every row is in the target: fill in the columns that were held back to break cycles.
                    _phase = TransferPhase.DeferredColumns;
                    _index = 0;
                    _after = null;
                    _afterGeneration = null;
                    continue;
                }
                var unit = await ReadBatchAsync(tables[_index], cancellationToken);
                if (unit is not null)
                    return unit;
                _index++;
                _after = null;
                _afterGeneration = null;
            }
        }

        private async Task<TransferUnit?> ReadBatchAsync(PlanTable planTable, CancellationToken cancellationToken)
        {
            var stableKey = StableKey(content, planTable);
            // The source shape is read once per table and phase, not once per batch.
            if (!_sources.TryGetValue((_phase, _index), out var source))
            {
                var sourceSchema = await new PostgreSqlTransferSchemaReader(dataSource).ReadAsync(
                    planTable.Mapping.Source.Schema,
                    planTable.Mapping.Source.Name,
                    stableKey.Columns,
                    cancellationToken
                );
                source = _sources[(_phase, _index)] =
                    _phase == TransferPhase.Rows
                        ? SourceTable(sourceSchema, planTable, stableKey)
                        : DeferredTable(sourceSchema, planTable, stableKey);
            }
            // Deferred columns leave the source as NULL in the rows phase; the second phase writes their values.
            // Hierarchy columns are held back only for rows the levelling could not reach (generation 0 or above).
            var withheld = Indexes(source, _phase == TransferPhase.Rows ? planTable.DeferredColumns : []);
            var withheldUnlevelled = Indexes(source, _phase == TransferPhase.Rows ? planTable.HierarchyColumns : []);
            var join =
                " JOIN "
                + PostgreSqlStagingTables.Qualified(
                    PostgreSqlStagingTables.SourceTableName(planId, planTable.Mapping.Source)
                )
                + " f ON f.__included AND "
                + string.Join(
                    " AND ",
                    source.StableKeyColumns.Select(
                        (column, index) => "s." + PostgreSqlIdentifier.Quote(column.Name) + "=f.k" + index
                    )
                );
            // Only hierarchy columns to fill in: just the rows the levelling could not reach need the second pass.
            if (_phase == TransferPhase.DeferredColumns && planTable.DeferredColumns.Count == 0)
                join += " AND f.__generation >= 0";
            if (_after is not null && _afterGeneration is null)
                _afterGeneration = await GenerationAsync(planTable, source, _after, cancellationToken);
            var query = PostgreSqlKeysetSeek.Build(
                source,
                _after,
                content.BatchTarget.MaximumRows,
                join,
                _afterGeneration
            );
            int? lastGeneration = null;
            var rows = new List<TransferRow>();
            StableKey? last = null;
            var bytes = 0L;
            await using var command = dataSource.CreateCommand(query.Sql);
            foreach (var parameter in query.Parameters)
                command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = source
                    .InsertColumns.Select((_, index) => reader.IsDBNull(index) ? null : reader.GetValue(index))
                    .ToArray();
                var generation = reader.GetInt32(source.InsertColumns.Count);
                foreach (var index in withheld)
                    values[index] = null;
                if (generation >= 0)
                    foreach (var index in withheldUnlevelled)
                        values[index] = null;
                var payload = values.Sum(PayloadBytes);
                if (rows.Count != 0 && bytes + payload > content.BatchTarget.MaximumBytes)
                    break;
                rows.Add(new TransferRow(values, payload));
                bytes += payload;
                lastGeneration = generation;
                last = new StableKey(
                    source.StableKeyColumns.Select(column => new KeyComponent(
                        column.Name,
                        values[Array.IndexOf(source.InsertColumns.ToArray(), column)]
                    ))
                );
            }
            if (rows.Count == 0)
                return null;
            _after = last;
            _afterGeneration = lastGeneration;
            return new TransferUnit(
                _nextSequence++,
                last!,
                rows.Count,
                _phase == TransferPhase.Rows ? TransferUnitKind.Batch : TransferUnitKind.DeferredColumns,
                bytes,
                planTable.Mapping.Source,
                rows
            );
        }

        private static int[] Indexes(PostgreSqlWriteTable source, IReadOnlyList<string> columns) =>
            source
                .InsertColumns.Select((column, index) => (column.Name, Index: index))
                .Where(item => columns.Contains(item.Name, DatabaseNames.Comparer))
                .Select(item => item.Index)
                .ToArray();

        /// <summary>The closure generation sealing stamped on a key; needed to resume generation-ordered paging.</summary>
        private async Task<int> GenerationAsync(
            PlanTable planTable,
            PostgreSqlWriteTable source,
            StableKey key,
            CancellationToken cancellationToken
        )
        {
            var predicate = string.Join(
                " AND ",
                source.StableKeyColumns.Select((_, index) => "k" + index + " = @p" + index)
            );
            await using var command = dataSource.CreateCommand(
                "SELECT __generation FROM "
                    + PostgreSqlStagingTables.Qualified(
                        PostgreSqlStagingTables.SourceTableName(planId, planTable.Mapping.Source)
                    )
                    + " WHERE "
                    + predicate
            );
            for (var index = 0; index < source.StableKeyColumns.Count; index++)
                command.Parameters.AddWithValue(
                    "p" + index,
                    key
                        .Components.Single(component =>
                            DatabaseNames.Equals(component.Column, source.StableKeyColumns[index].Name)
                        )
                        .Value!
                );
            return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        public Task DiscardUncommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => dataSource.DisposeAsync();
    }

    private sealed class TargetSession(NpgsqlDataSource dataSource, TransferPlanContent content) : ITargetRunSession
    {
        private readonly PostgreSqlTargetCheckpointStore _checkpoints = new(dataSource);
        private readonly PostgreSqlTransferExecutor _executor = new(dataSource, new NoopMirror(), new NoopBarrier());
        private long _bytesTransferred;
        private long _skippedRows;
        private readonly Dictionary<(string Schema, string Name), PostgreSqlWriteTable> _targets = [];

        /// <summary>The target shape is read once per table for the session, not once per batch.</summary>
        private async Task<PostgreSqlWriteTable> TargetAsync(
            PlanTable planTable,
            string[] targetKeys,
            CancellationToken cancellationToken
        )
        {
            var address = (planTable.Mapping.Target.Schema, planTable.Mapping.Target.Name);
            if (!_targets.TryGetValue(address, out var target))
                target = _targets[address] = Mapped(
                    await new PostgreSqlTransferSchemaReader(dataSource).ReadAsync(
                        planTable.Mapping.Target.Schema,
                        planTable.Mapping.Target.Name,
                        targetKeys,
                        cancellationToken
                    ),
                    planTable
                );
            return target;
        }

        /// <summary>
        /// Only the target columns the plan maps are written; the rest keep their defaults. A unique key over an
        /// unmapped column cannot be judged from the rows and is left to the target.
        /// </summary>
        private static PostgreSqlWriteTable Mapped(PostgreSqlWriteTable shape, PlanTable planTable)
        {
            var mapped = planTable.Mapping.Columns.Select(mapping => mapping.Target).ToHashSet(DatabaseNames.Comparer);
            return new PostgreSqlWriteTable(
                shape.Target,
                shape.Columns.Where(column => mapped.Contains(column.Name)),
                shape
                    .UniqueKeys.Where(key => key.All(column => mapped.Contains(column.Name)))
                    .Select(key => key.Select(column => column.Name).ToArray())
            );
        }

        public async Task<RecoverySnapshot> AcquireFenceReadCheckpointAndJournalAsync(
            TransferRun run,
            LeaseGrant lease,
            CancellationToken cancellationToken
        )
        {
            var context = Context(run, lease);
            await _executor.InitializeAsync(context, cancellationToken);
            var checkpoint =
                await _checkpoints.ReadAsync(run.JobId, run.RunId, cancellationToken)
                ?? throw new InvalidOperationException("Target checkpoint was not initialized.");
            return new RecoverySnapshot(await CheckpointAsync(run, checkpoint, cancellationToken), []);
        }

        // Mutation journal recovery is unwired for PostgreSQL as well; add it when mutation strategies are enabled.
        public Task<IReadOnlyList<MutationJournalEntry>> RepairMutationsAsync(
            IReadOnlyList<MutationJournalEntry> mutations,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<MutationJournalEntry>>([]);

        public Task QuarantineAsync(TargetMutation mutation, string reason, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task<TargetCheckpoint> ApplyAsync(
            TransferRun run,
            LeaseGrant lease,
            TransferUnit unit,
            CancellationToken cancellationToken
        )
        {
            var source = unit.Table ?? throw new InvalidOperationException("Transfer unit table was not provided.");
            var rows = unit.Rows ?? throw new InvalidOperationException("Transfer unit rows were not provided.");
            var planTable = Tables(content).Single(item => Same(item.Mapping.Source, source));
            var stableKey = StableKey(content, planTable);
            var mappings = planTable.Mapping.Columns;
            var targetKeys = stableKey
                .Columns.Select(column =>
                    mappings.Single(mapping => DatabaseNames.Equals(mapping.Source, column)).Target
                )
                .ToArray();
            var target = await TargetAsync(planTable, targetKeys, cancellationToken);
            PostgreSqlBatchCommit commit;
            if (unit.Kind == TransferUnitKind.DeferredColumns)
            {
                // Stable key plus the deferred columns only: the values held back to break a cycle.
                var deferredTargets = planTable
                    .BackfilledColumns.Select(column =>
                        mappings.Single(mapping => DatabaseNames.Equals(mapping.Source, column)).Target
                    )
                    .ToArray();
                var subset = new PostgreSqlWriteTable(
                    target.Target,
                    target.Columns.Where(column =>
                        column.IsStableKey || deferredTargets.Contains(column.Name, DatabaseNames.Comparer)
                    )
                );
                var deferredSources = stableKey.Columns.Concat(planTable.BackfilledColumns).ToArray();
                var backfill = new PostgreSqlTransferBatch(
                    BatchSequence.ProviderFromWorker(unit.BatchSequence),
                    rows.Select(row => TargetRow(row, deferredSources, mappings, subset)),
                    TargetKey(unit.LastStableKey, mappings),
                    Policy(content, planTable)
                );
                commit = await _executor.BackfillAsync(Context(run, lease), subset, backfill, cancellationToken);
                _bytesTransferred += unit.BytesTransferred;
            }
            else
            {
                var sourceColumns = SourceColumns(planTable, stableKey);
                var batch = new PostgreSqlTransferBatch(
                    BatchSequence.ProviderFromWorker(unit.BatchSequence),
                    rows.Select(row => TargetRow(row, sourceColumns, mappings, target)),
                    TargetKey(unit.LastStableKey, mappings),
                    Policy(content, planTable)
                );
                commit = await _executor.ExecuteAsync(Context(run, lease), target, batch, cancellationToken);
                _bytesTransferred += unit.BytesTransferred;
                _skippedRows += Math.Max(0, rows.Count - commit.Affected);
            }
            var checkpoint =
                commit.Checkpoint
                ?? await _checkpoints.ReadAsync(run.JobId, run.RunId, cancellationToken)
                ?? throw new InvalidOperationException("Committed target checkpoint was not found.");
            return await CheckpointAsync(run, checkpoint, cancellationToken);
        }

        public Task DiscardUncommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task VerifyAsync(TransferRun run, LeaseGrant lease, CancellationToken cancellationToken) =>
            new PostgreSqlTransferVerifier(dataSource).VerifyAsync(content, Context(run, lease), cancellationToken);

        public ValueTask DisposeAsync() => dataSource.DisposeAsync();

        private async Task<TargetCheckpoint> CheckpointAsync(
            TransferRun run,
            PostgreSqlTargetCheckpoint checkpoint,
            CancellationToken cancellationToken
        )
        {
            if (checkpoint.LastBatchSequence < 0)
                return new TargetCheckpoint(
                    run.JobId,
                    run.RunId,
                    BatchSequence.WorkerFromProvider(checkpoint.LastBatchSequence),
                    null,
                    checkpoint.CumulativeAffected,
                    run.ManifestSealHash,
                    checkpoint.FenceToken,
                    _bytesTransferred,
                    SkippedRows: _skippedRows,
                    Phase: (TransferPhase)checkpoint.Phase
                );
            var planTable = checkpoint.LastTable is null
                ? Tables(content).Single()
                : Tables(content).Single(item => Same(item.Mapping.Target, checkpoint.LastTable));
            var stableKey = StableKey(content, planTable);
            var targetKeys = stableKey
                .Columns.Select(column =>
                    planTable.Mapping.Columns.Single(mapping => DatabaseNames.Equals(mapping.Source, column)).Target
                )
                .ToArray();
            var target = await TargetAsync(planTable, targetKeys, cancellationToken);
            var key = PostgreSqlStableKeyCodec.Decode(checkpoint.LastStableKey, target);
            return new TargetCheckpoint(
                run.JobId,
                run.RunId,
                BatchSequence.WorkerFromProvider(checkpoint.LastBatchSequence),
                SourceKey(key, planTable.Mapping.Columns),
                checkpoint.CumulativeAffected,
                run.ManifestSealHash,
                checkpoint.FenceToken,
                _bytesTransferred,
                planTable.Mapping.Source,
                _skippedRows,
                (TransferPhase)checkpoint.Phase
            );
        }
    }

    // Provider checkpoints use -1/0 while worker checkpoints use 0/1.
    private static class BatchSequence
    {
        public static long WorkerFromProvider(long providerSequence) => checked(providerSequence + 1);

        public static long ProviderFromWorker(long workerSequence) => checked(workerSequence - 1);
    }

    private static IReadOnlyList<PlanTable> Tables(TransferPlanContent content)
    {
        var planned = content.Tables.Where(table => table.Manifest.PlannedWrites > 0).ToArray();
        var ordered = new List<PlanTable>();
        foreach (var address in content.Tables.SelectMany(table => table.TopologicalGroup.Tables))
        {
            var table = planned.SingleOrDefault(item => Same(item.Mapping.Source, address));
            if (table is not null && !ordered.Contains(table))
                ordered.Add(table);
        }
        ordered.AddRange(planned.Where(table => !ordered.Contains(table)));
        return ordered;
    }

    /// <summary>Tables with columns held back to break a cycle, in write order; the second phase fills them in.</summary>
    private static IReadOnlyList<PlanTable> Deferred(TransferPlanContent content) =>
        Tables(content).Where(table => table.BackfilledColumns.Count > 0).ToArray();

    private static PostgreSqlWriteTable DeferredTable(
        PostgreSqlWriteTable schema,
        PlanTable table,
        StableKeyDefinition stableKey
    ) => new(schema.Target, stableKey.Columns.Concat(table.BackfilledColumns).Select(schema.Column));

    private static int StartIndex(IReadOnlyList<PlanTable> tables, TableAddress? startTable) =>
        startTable is null
            ? 0
            : Enumerable.Range(0, tables.Count).Single(index => Same(tables[index].Mapping.Source, startTable));

    private static StableKeyDefinition StableKey(TransferPlanContent content, PlanTable table) =>
        content.StableKeys.Single(key => Same(key.Table, table.Mapping.Source));

    private static PostgreSqlWriteTable SourceTable(
        PostgreSqlWriteTable schema,
        PlanTable table,
        StableKeyDefinition stableKey
    ) =>
        new(
            schema.Target,
            stableKey
                .Columns.Select(schema.Column)
                .Concat(
                    table
                        .Mapping.Columns.Where(mapping =>
                            !stableKey.Columns.Contains(mapping.Source, DatabaseNames.Comparer)
                        )
                        .Select(mapping => schema.Column(mapping.Source))
                )
        );

    private static string[] SourceColumns(PlanTable table, StableKeyDefinition stableKey) =>
        stableKey
            .Columns.Concat(
                table
                    .Mapping.Columns.Where(mapping =>
                        !stableKey.Columns.Contains(mapping.Source, DatabaseNames.Comparer)
                    )
                    .Select(mapping => mapping.Source)
            )
            .ToArray();

    private static long PayloadBytes(object? value) =>
        value switch
        {
            string text => Encoding.UTF8.GetByteCount(text),
            byte[] bytes => bytes.LongLength,
            int => sizeof(int),
            long => sizeof(long),
            Guid => 16,
            _ => 0,
        };

    private static bool Same(TableAddress left, TableAddress right) =>
        DatabaseNames.Equals(left.Schema, right.Schema) && DatabaseNames.Equals(left.Name, right.Name);

    private static PostgreSqlExecutionContext Context(TransferRun run, LeaseGrant lease) =>
        new(run.JobId, run.RunId, lease.FenceToken, run.ManifestSealHash);

    private static PostgreSqlConflictPolicy Policy(TransferPlanContent content, PlanTable table) =>
        content.ConflictPolicies.SingleOrDefault(policy => Same(policy.Table, table.Mapping.Source))?.Policy switch
        {
            RootConflictPolicy.SkipExisting => PostgreSqlConflictPolicy.SkipExisting,
            RootConflictPolicy.Upsert => PostgreSqlConflictPolicy.Upsert,
            // Rows the target already has are skipped, never a failure: parents pulled in by the graph are often
            // reference data that exists on both sides, and the root's existence was settled at sealing time.
            _ => PostgreSqlConflictPolicy.SkipExisting,
        };

    private static StableKey TargetKey(StableKey source, IReadOnlyList<ColumnMapping> mappings) =>
        new(
            source.Components.Select(component => new KeyComponent(
                mappings.Single(mapping => DatabaseNames.Equals(mapping.Source, component.Column)).Target,
                component.Value
            ))
        );

    private static StableKey SourceKey(StableKey target, IReadOnlyList<ColumnMapping> mappings) =>
        new(
            target.Components.Select(component => new KeyComponent(
                mappings.Single(mapping => DatabaseNames.Equals(mapping.Target, component.Column)).Source,
                component.Value
            ))
        );

    private static PostgreSqlTransferRow TargetRow(
        TransferRow row,
        IReadOnlyList<string> sourceColumns,
        IReadOnlyList<ColumnMapping> mappings,
        PostgreSqlWriteTable target
    )
    {
        var values = target.InsertColumns.ToDictionary(
            column => column.Name,
            column =>
                row.Values[
                    Array.IndexOf(
                        sourceColumns.ToArray(),
                        mappings.Single(mapping => DatabaseNames.Equals(mapping.Target, column.Name)).Source
                    )
                ],
            DatabaseNames.Comparer
        );
        return new PostgreSqlTransferRow(
            new StableKey(target.StableKeyColumns.Select(column => new KeyComponent(column.Name, values[column.Name]))),
            values
        );
    }

    private sealed class NoopMirror : IDerivedCheckpointMirror
    {
        public Task WriteAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoopBarrier : IAfterTargetCommitBarrier
    {
        public Task WaitAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
