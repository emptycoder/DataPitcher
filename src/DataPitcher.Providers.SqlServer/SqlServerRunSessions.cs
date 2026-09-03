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
        TableAddress? table = null
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
            BatchSequence.WorkerFromProvider(checkpoint.LastBatchSequence) + 1
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
        long nextSequence
    ) : ITransferReadSession
    {
        private readonly IReadOnlyList<PlanTable> _tables = Tables(content);
        private readonly Dictionary<int, SqlServerWriteTable> _sources = [];
        private int _index = StartIndex(Tables(content), startTable);
        private StableKey? _after = startAfter;
        private long _nextSequence = nextSequence;

        public async Task<TransferUnit?> ReadNextAsync(CancellationToken cancellationToken)
        {
            while (_index < _tables.Count)
            {
                var planTable = _tables[_index];
                var stableKey = StableKey(content, planTable);
                // The source shape is read once per table, not once per batch.
                if (!_sources.TryGetValue(_index, out var source))
                {
                    var sourceSchema = await new SqlServerTransferSchemaReader(connectionString).ReadAsync(
                        planTable.Mapping.Source.Schema,
                        planTable.Mapping.Source.Name,
                        stableKey.Columns,
                        cancellationToken
                    );
                    source = _sources[_index] = SourceTable(sourceSchema, planTable, stableKey);
                }
                var join =
                    " JOIN "
                    + SqlServerStagingTables.Qualified(
                        SqlServerStagingTables.SourceTableName(planId, planTable.Mapping.Source)
                    )
                    + " f ON "
                    + string.Join(
                        " AND ",
                        source.StableKeyColumns.Select(
                            (column, index) => "s." + SqlServerIdentifier.Quote(column.Name) + "=f.[k" + index + "]"
                        )
                    );
                var query = SqlServerKeysetSeek.Build(source, _after, content.BatchTarget.MaximumRows, join);
                var rows = new List<TransferRow>();
                StableKey? last = null;
                var bytes = 0L;
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand(query.Sql, connection);
                command.Parameters.AddRange(query.Parameters.ToArray());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var values = source
                        .InsertColumns.Select((_, index) => reader.IsDBNull(index) ? null : reader.GetValue(index))
                        .ToArray();
                    var payload = values.Sum(PayloadBytes);
                    if (rows.Count != 0 && bytes + payload > content.BatchTarget.MaximumBytes)
                        break;
                    rows.Add(new TransferRow(values, payload));
                    bytes += payload;
                    last = new StableKey(
                        source.StableKeyColumns.Select(column => new KeyComponent(
                            column.Name,
                            values[Array.IndexOf(source.InsertColumns.ToArray(), column)]
                        ))
                    );
                }

                if (rows.Count != 0)
                {
                    _after = last;
                    return new TransferUnit(
                        _nextSequence++,
                        last!,
                        rows.Count,
                        TransferUnitKind.Batch,
                        bytes,
                        planTable.Mapping.Source,
                        rows
                    );
                }
                _index++;
                _after = null;
            }
            return null;
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

        /// <summary>The target shape is read once per table for the session, not once per batch.</summary>
        private async Task<SqlServerWriteTable> TargetAsync(
            PlanTable planTable,
            string[] targetKeys,
            CancellationToken cancellationToken
        )
        {
            var address = (planTable.Mapping.Target.Schema, planTable.Mapping.Target.Name);
            if (!_targets.TryGetValue(address, out var target))
                target = _targets[address] = await new SqlServerTransferSchemaReader(connectionString).ReadAsync(
                    planTable.Mapping.Target.Schema,
                    planTable.Mapping.Target.Name,
                    targetKeys,
                    cancellationToken
                );
            return target;
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
                    mappings.Single(mapping => StringComparer.Ordinal.Equals(mapping.Source, column)).Target
                )
                .ToArray();
            var target = await TargetAsync(planTable, targetKeys, cancellationToken);
            var sourceColumns = SourceColumns(planTable, stableKey);
            var batch = new SqlServerTransferBatch(
                BatchSequence.ProviderFromWorker(unit.BatchSequence),
                rows.Select(row => TargetRow(row, sourceColumns, mappings, target)),
                TargetKey(unit.LastStableKey, mappings),
                Policy(content, planTable)
            );
            var commit = await _executor.ExecuteAsync(Context(run, lease), target, batch, cancellationToken);
            _bytesTransferred += unit.BytesTransferred;
            var checkpoint =
                commit.Checkpoint
                ?? await _checkpoints.ReadAsync(run.JobId, run.RunId, cancellationToken)
                ?? throw new InvalidOperationException("Committed target checkpoint was not found.");
            return await CheckpointAsync(run, checkpoint, cancellationToken);
        }

        public Task DiscardUncommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
                    _bytesTransferred
                );
            var planTable = checkpoint.LastTable is null
                ? Tables(content).Single()
                : Tables(content).Single(item => Same(item.Mapping.Target, checkpoint.LastTable));
            var stableKey = StableKey(content, planTable);
            var targetKeys = stableKey
                .Columns.Select(column =>
                    planTable
                        .Mapping.Columns.Single(mapping => StringComparer.Ordinal.Equals(mapping.Source, column))
                        .Target
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
                planTable.Mapping.Source
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
                            !stableKey.Columns.Contains(mapping.Source, StringComparer.Ordinal)
                        )
                        .Select(mapping => schema.Column(mapping.Source))
                )
        );

    private static string[] SourceColumns(PlanTable table, StableKeyDefinition stableKey) =>
        stableKey
            .Columns.Concat(
                table
                    .Mapping.Columns.Where(mapping =>
                        !stableKey.Columns.Contains(mapping.Source, StringComparer.Ordinal)
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
            _ => 0,
        };

    private static bool Same(TableAddress left, TableAddress right) =>
        StringComparer.Ordinal.Equals(left.Schema, right.Schema)
        && StringComparer.Ordinal.Equals(left.Name, right.Name);

    private static SqlServerExecutionContext Context(TransferRun run, LeaseGrant lease) =>
        new(run.JobId, run.RunId, lease.FenceToken, run.ManifestSealHash);

    private static SqlServerConflictPolicy Policy(TransferPlanContent content, PlanTable table) =>
        content.ConflictPolicies.SingleOrDefault(policy => Same(policy.Table, table.Mapping.Source))?.Policy switch
        {
            RootConflictPolicy.SkipExisting => SqlServerConflictPolicy.SkipExisting,
            RootConflictPolicy.Upsert => SqlServerConflictPolicy.Upsert,
            _ => SqlServerConflictPolicy.InsertOnly,
        };

    private static StableKey TargetKey(StableKey source, IReadOnlyList<ColumnMapping> mappings) =>
        new(
            source.Components.Select(component => new KeyComponent(
                mappings.Single(mapping => StringComparer.Ordinal.Equals(mapping.Source, component.Column)).Target,
                component.Value
            ))
        );

    private static StableKey SourceKey(StableKey target, IReadOnlyList<ColumnMapping> mappings) =>
        new(
            target.Components.Select(component => new KeyComponent(
                mappings.Single(mapping => StringComparer.Ordinal.Equals(mapping.Target, component.Column)).Source,
                component.Value
            ))
        );

    private static SqlServerTransferRow TargetRow(
        TransferRow row,
        IReadOnlyList<string> sourceColumns,
        IReadOnlyList<ColumnMapping> mappings,
        SqlServerWriteTable target
    )
    {
        var values = target.InsertColumns.ToDictionary(
            column => column.Name,
            column =>
                row.Values[
                    Array.IndexOf(
                        sourceColumns.ToArray(),
                        mappings.Single(mapping => StringComparer.Ordinal.Equals(mapping.Target, column.Name)).Source
                    )
                ],
            StringComparer.Ordinal
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
