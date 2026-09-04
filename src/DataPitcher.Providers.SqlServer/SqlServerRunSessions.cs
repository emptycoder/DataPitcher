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
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerRunSessions(
    IPlanRepository plans,
    IConnectionProfileRepository profiles,
    ISecretReferenceResolver secrets
) : IRunSessionProvider
{
    static SqlServerRunSessions() => SqlServerEntraAuthentication.EnsureRegistered();

    public string ProviderId => "sqlserver";

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
        var target = await ConnectionStringAsync(run.TargetConnectionId, cancellationToken);
        var checkpoint =
            await new SqlServerTargetCheckpointStore(target).ReadAsync(run.JobId, run.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Target checkpoint was not initialized.");
        return new ReadSession(
            source,
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
        return new TargetSession(await ConnectionStringAsync(run.TargetConnectionId, cancellationToken), content);
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
        string connectionString,
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
        private readonly Dictionary<(TransferPhase Phase, int Index), SqlServerWriteTable> _sources = [];
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
                var sourceSchema = await new SqlServerTransferSchemaReader(connectionString).ReadAsync(
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
                + SqlServerStagingTables.Qualified(
                    SqlServerStagingTables.SourceTableName(planId, planTable.Mapping.Source)
                )
                + " f ON f.[__included]=1 AND "
                + string.Join(
                    " AND ",
                    source.StableKeyColumns.Select(
                        (column, index) => "s." + SqlServerIdentifier.Quote(column.Name) + "=f.[k" + index + "]"
                    )
                );
            // Only hierarchy columns to fill in: just the rows the levelling could not reach need the second pass.
            if (_phase == TransferPhase.DeferredColumns && planTable.DeferredColumns.Count == 0)
                join += " AND f.[__generation]>=0";
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            if (_after is not null && _afterGeneration is null)
                _afterGeneration = await GenerationAsync(connection, planTable, source, _after, cancellationToken);
            var query = SqlServerKeysetSeek.Build(
                source,
                _after,
                content.BatchTarget.MaximumRows,
                join,
                _afterGeneration
            );
            var rows = new List<TransferRow>();
            StableKey? last = null;
            int? lastGeneration = null;
            var bytes = 0L;
            await using var command = new SqlCommand(query.Sql, connection);
            command.Parameters.AddRange(query.Parameters.ToArray());
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
                rows,
                source.InsertColumns.Select(column => column.Name).ToArray()
            );
        }

        private static int[] Indexes(SqlServerWriteTable source, IReadOnlyList<string> columns) =>
            source
                .InsertColumns.Select((column, index) => (column.Name, Index: index))
                .Where(item => columns.Contains(item.Name, DatabaseNames.Comparer))
                .Select(item => item.Index)
                .ToArray();

        /// <summary>The closure generation sealing stamped on a key; needed to resume generation-ordered paging.</summary>
        private async Task<int> GenerationAsync(
            SqlConnection connection,
            PlanTable planTable,
            SqlServerWriteTable source,
            StableKey key,
            CancellationToken cancellationToken
        )
        {
            var predicate = string.Join(
                " AND ",
                source.StableKeyColumns.Select((_, index) => "[k" + index + "]=@p" + index)
            );
            await using var command = new SqlCommand(
                "SELECT [__generation] FROM "
                    + SqlServerStagingTables.Qualified(
                        SqlServerStagingTables.SourceTableName(planId, planTable.Mapping.Source)
                    )
                    + " WHERE "
                    + predicate,
                connection
            );
            for (var index = 0; index < source.StableKeyColumns.Count; index++)
                command.Parameters.AddWithValue(
                    "@p" + index,
                    key
                        .Components.Single(component =>
                            DatabaseNames.Equals(component.Column, source.StableKeyColumns[index].Name)
                        )
                        .Value!
                );
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        public Task DiscardUncommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TargetSession(string connectionString, TransferPlanContent content) : ITargetRunSession
    {
        private readonly SqlServerTargetCheckpointStore _checkpoints = new(connectionString);
        private readonly SqlServerTransferExecutor _executor = new(
            connectionString,
            new NoopMirror(),
            new NoopBarrier()
        );
        private readonly Dictionary<(string Schema, string Name), SqlServerWriteTable> _targets = [];
        private long _bytesTransferred;
        private long _skippedRows;

        /// <summary>The target shape is read once per table for the session, not once per batch.</summary>
        private async Task<SqlServerWriteTable> TargetAsync(
            PlanTable planTable,
            string[] targetKeys,
            CancellationToken cancellationToken
        )
        {
            var address = (planTable.Mapping.Target.Schema, planTable.Mapping.Target.Name);
            if (!_targets.TryGetValue(address, out var target))
                target = _targets[address] = Mapped(
                    await new SqlServerTransferSchemaReader(connectionString).ReadAsync(
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
        private static SqlServerWriteTable Mapped(SqlServerWriteTable shape, PlanTable planTable)
        {
            var mapped = planTable.Mapping.Columns.Select(mapping => mapping.Target).ToHashSet(DatabaseNames.Comparer);
            return new SqlServerWriteTable(
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

        // ponytail: mutation journal recovery is unwired; add it when mutation strategies are enabled.
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
            SqlServerBatchCommit commit;
            if (unit.Kind == TransferUnitKind.DeferredColumns)
            {
                // Stable key plus the deferred columns only: the values held back to break a cycle.
                var deferredTargets = planTable
                    .BackfilledColumns.Select(column =>
                        mappings.Single(mapping => DatabaseNames.Equals(mapping.Source, column)).Target
                    )
                    .ToArray();
                var subset = new SqlServerWriteTable(
                    target.Target,
                    target.Columns.Where(column =>
                        column.IsStableKey || deferredTargets.Contains(column.Name, DatabaseNames.Comparer)
                    )
                );
                var backfill = new SqlServerTransferBatch(
                    BatchSequence.ProviderFromWorker(unit.BatchSequence),
                    rows.Select(row => TargetRow(unit, row, mappings, subset)),
                    TargetKey(unit.LastStableKey, mappings),
                    Policy(content, planTable)
                );
                commit = await _executor.BackfillAsync(Context(run, lease), subset, backfill, cancellationToken);
                _bytesTransferred += unit.BytesTransferred;
            }
            else
            {
                var batch = new SqlServerTransferBatch(
                    BatchSequence.ProviderFromWorker(unit.BatchSequence),
                    rows.Select(row => TargetRow(unit, row, mappings, target)),
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
            new SqlServerTransferVerifier(connectionString).VerifyAsync(
                content,
                Context(run, lease),
                cancellationToken
            );

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<TargetCheckpoint> CheckpointAsync(
            TransferRun run,
            SqlServerTargetCheckpoint checkpoint,
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
            var key = SqlServerStableKeyCodec.Decode(checkpoint.LastStableKey, target);
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

    private static SqlServerWriteTable DeferredTable(
        SqlServerWriteTable schema,
        PlanTable table,
        StableKeyDefinition stableKey
    ) => new(schema.Target, stableKey.Columns.Concat(table.BackfilledColumns).Select(schema.Column));

    private static int StartIndex(IReadOnlyList<PlanTable> tables, TableAddress? startTable) =>
        startTable is null
            ? 0
            : Enumerable.Range(0, tables.Count).Single(index => Same(tables[index].Mapping.Source, startTable));

    private static StableKeyDefinition StableKey(TransferPlanContent content, PlanTable table) =>
        content.StableKeys.Single(key => Same(key.Table, table.Mapping.Source));

    private static SqlServerWriteTable SourceTable(
        SqlServerWriteTable schema,
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

    private static long PayloadBytes(object? value) =>
        value switch
        {
            string text => Encoding.UTF8.GetByteCount(text),
            byte[] bytes => bytes.LongLength,
            int => sizeof(int),
            long => sizeof(long),
            _ => 0,
        };

    private static bool Same(TableAddress left, TableAddress right) =>
        DatabaseNames.Equals(left.Schema, right.Schema) && DatabaseNames.Equals(left.Name, right.Name);

    private static SqlServerExecutionContext Context(TransferRun run, LeaseGrant lease) =>
        new(run.JobId, run.RunId, lease.FenceToken, run.ManifestSealHash);

    private static SqlServerConflictPolicy Policy(TransferPlanContent content, PlanTable table) =>
        content.ConflictPolicies.SingleOrDefault(policy => Same(policy.Table, table.Mapping.Source))?.Policy switch
        {
            RootConflictPolicy.SkipExisting => SqlServerConflictPolicy.SkipExisting,
            RootConflictPolicy.Upsert => SqlServerConflictPolicy.Upsert,
            // Rows the target already has are skipped, never a failure: parents pulled in by the graph are often
            // reference data that exists on both sides, and the root's existence was settled at sealing time.
            _ => SqlServerConflictPolicy.SkipExisting,
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

    private static SqlServerTransferRow TargetRow(
        TransferUnit unit,
        TransferRow row,
        IReadOnlyList<ColumnMapping> mappings,
        SqlServerWriteTable target
    )
    {
        var values = target.InsertColumns.ToDictionary(
            column => column.Name,
            column =>
                unit.Value(row, mappings.Single(mapping => DatabaseNames.Equals(mapping.Target, column.Name)).Source),
            DatabaseNames.Comparer
        );
        return new SqlServerTransferRow(
            new StableKey(target.StableKeyColumns.Select(column => new KeyComponent(column.Name, values[column.Name]))),
            values
        );
    }

    private sealed class NoopMirror : ISqlServerDerivedCheckpointMirror
    {
        public Task WriteAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoopBarrier : ISqlServerAfterTargetCommitBarrier
    {
        public Task WaitAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
