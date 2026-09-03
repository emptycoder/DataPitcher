using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataPitcher.Application.Connections;
using DataPitcher.Application.Schema;
using DataPitcher.Application.Selection;
using DataPitcher.Core.Closure;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Application.Plans;

/// <summary>
/// Provider-neutral plan sealing: validates the saved selection against the sealed schema snapshot, runs it against
/// the source, computes the demand-driven closure across source and target through the provider's
/// <see cref="ISealingSession"/>, and freezes the resulting content on the plan.
/// </summary>
public sealed class PlanSealingService(
    IPlanRepository plans,
    ISelectionRepository selections,
    IConnectionProfileRepository connections,
    ISchemaSnapshotRepository snapshots,
    ISecretReferenceResolver secrets,
    IEnumerable<ISealingProvider> providers
)
{
    private readonly IReadOnlyDictionary<string, ISealingProvider> providers = providers.ToDictionary(
        provider => provider.ProviderId,
        StringComparer.Ordinal
    );

    public async Task SealAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan =
            await plans.FindAsync(planId, cancellationToken)
            ?? throw new InvalidOperationException("Plan was not found.");
        if (
            plan.SelectionId is not Guid selectionId
            || plan.SourceConnectionId is not Guid sourceConnectionId
            || plan.TargetConnectionId is not Guid targetConnectionId
        )
            throw new InvalidOperationException(
                "Plan sealing requires a selection, source connection, and target connection."
            );
        var selection =
            await selections.FindAsync(selectionId, cancellationToken)
            ?? throw new InvalidOperationException("Selection was not found.");
        if (
            selection.ConnectionId != sourceConnectionId
            || selection.SnapshotId is not Guid snapshotId
            || string.IsNullOrWhiteSpace(selection.RootSchema)
            || string.IsNullOrWhiteSpace(selection.RootTable)
            || string.IsNullOrWhiteSpace(selection.StableKeyConstraintName)
            || selection.StableKeyColumns is not { Count: > 0 }
        )
            throw new InvalidOperationException(
                "Selection root table, stable key, source connection, and snapshot are required for sealing."
            );
        var source = await connections.GetProfileAsync(sourceConnectionId, cancellationToken);
        var target = await connections.GetProfileAsync(targetConnectionId, cancellationToken);
        if (!string.Equals(source.ProviderId, target.ProviderId, StringComparison.Ordinal))
            throw new NotSupportedException(
                "Cross-provider transfers are blocked: the source and target must use the same database provider."
            );
        if (!providers.TryGetValue(source.ProviderId, out var provider))
            throw new NotSupportedException($"Plan sealing is not available for the '{source.ProviderId}' provider.");
        var snapshot = await snapshots.GetAsync(sourceConnectionId, snapshotId, cancellationToken);
        var sourceConnection = await secrets.ResolveAsync(source.SecretReference, cancellationToken);
        var targetConnection = await secrets.ResolveAsync(target.SecretReference, cancellationToken);
        await using var session = await provider.OpenAsync(
            source,
            sourceConnection,
            target,
            targetConnection,
            cancellationToken
        );
        if (
            !string.Equals(
                CanonicalSchemaSnapshotHasher.Hash(session.SourceSchema),
                snapshot.Hash,
                StringComparison.Ordinal
            )
        )
            throw new InvalidOperationException("Source schema changed since the selection snapshot.");
        var root =
            session.SourceTables.SingleOrDefault(table =>
                string.Equals(table.Schema, selection.RootSchema, StringComparison.Ordinal)
                && string.Equals(table.Name, selection.RootTable, StringComparison.Ordinal)
            ) ?? throw new InvalidOperationException("Selection root table was not found in the source catalog.");
        var rootKey = StableKey(root, selection.StableKeyConstraintName, selection.StableKeyColumns);
        var (rawSql, parameters, parameterHash) = RawSql(selection.QueryJson);
        var sql = new GeneratedSelectionSql(rawSql, root, rootKey, parameters, true);
        await session.ValidateAsync(sql, cancellationToken);
        var seeds = await session.ReadKeysAsync(
            sql,
            SelectionExecutionLimits.Default.MaximumResultSize,
            cancellationToken
        );
        var relationships = ReachableRelationships(session.SourceForeignKeys, root);
        var stableKeys = relationships
            .SelectMany(relationship => new[] { relationship.FromTable, relationship.ToTable })
            .Append(root)
            .Distinct()
            .ToDictionary(table => table, table => StableKeySelector.Select(table, null));
        stableKeys[root] = new StableKeySelection(rootKey);
        var store = session.CreateClosureStore(stableKeys, planId);
        ClosureResult closure;
        try
        {
            closure = await new DependencyClosure(store).ComputeAsync(
                new ClosureRequest(
                    [new ClosureRoot(root, seeds.Keys, RootConflictPolicy.SkipExisting)],
                    relationships,
                    stableKeys
                ),
                cancellationToken
            );
        }
        finally
        {
            if (store is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
        var content = Content(
            selection,
            snapshot,
            session.SourceSchema,
            session.TargetSchema,
            source,
            target,
            root,
            relationships,
            stableKeys,
            closure,
            parameterHash
        );
        var sealedPlan = new TransferPlanLifecycle(
            new TransferPlanDraft(plan.DisplayName, plan.OperatorNote, "", plan.UpdatedUtc, content)
        ).Seal(planId, DateTimeOffset.UtcNow);
        await plans.SealAsync(planId, sealedPlan.Content, cancellationToken);
    }

    private static TransferPlanContent Content(
        SelectionRecord selection,
        StoredSchemaSnapshot snapshot,
        SchemaSnapshotContent sourceSchema,
        SchemaSnapshotContent targetSchema,
        ConnectionProfile source,
        ConnectionProfile target,
        TableDefinition root,
        IReadOnlyCollection<ClosureRelationship> relationships,
        IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
        ClosureResult closure,
        string parameterHash
    )
    {
        var groups = closure.Rows.GroupBy(row => row.Table).ToArray();
        var order = TransferOrder(
            groups.Select(group => group.Key).ToArray(),
            relationships,
            groups.ToDictionary(group => group.Key, group => group.Max(row => row.Generation))
        );
        var tables = groups
            .OrderBy(group => order[group.Key])
            .Select(group =>
            {
                var address = Address(group.Key);
                var count = group.LongCount();
                return new PlanTable(
                    new TableMapping(
                        address,
                        address,
                        group.Key.Columns.Select(column => new ColumnMapping(column.Name, column.Name)).ToArray()
                    ),
                    group.Key == root ? PlanTableState.Root : PlanTableState.RequiredDependency,
                    new ManifestCounts(count, count, count, 0),
                    new TopologicalGroup([address]),
                    CycleStrategy.NotApplicable
                );
            })
            .ToArray();
        var totals = tables.Aggregate(
            new ManifestCounts(0, 0, 0, 0),
            (current, table) =>
                new ManifestCounts(
                    current.Included + table.Manifest.Included,
                    current.PlannedWrites + table.Manifest.PlannedWrites,
                    current.Inserts + table.Manifest.Inserts,
                    current.Updates + table.Manifest.Updates
                )
        );
        return new TransferPlanContent(
            new ConnectionFingerprint(
                source.ProviderId,
                sourceSchema.DatabaseIdentity,
                snapshot.Hash,
                source.ConnectionId
            ),
            new ConnectionFingerprint(
                target.ProviderId,
                targetSchema.DatabaseIdentity,
                CanonicalSchemaSnapshotHasher.Hash(targetSchema),
                target.ConnectionId
            ),
            new SchemaSnapshotReference(snapshot.Hash),
            new SchemaSnapshotReference(CanonicalSchemaSnapshotHasher.Hash(targetSchema)),
            [new SelectionReference(selection.SelectionId, selection.Version, parameterHash)],
            relationships
                .Select(relationship => new RelationshipPolicy(
                    relationship.Name,
                    Address(relationship.FromTable),
                    Address(relationship.ToTable),
                    relationship.FromColumns,
                    relationship.ToColumns,
                    Core.Plans.RelationshipDirection.Outbound,
                    true
                ))
                .ToArray(),
            [new TableConflictPolicy(Address(root), RootConflictPolicy.SkipExisting)],
            ConsistencyMode.FrozenKeys,
            TransferMode.ResumableStaged,
            TriggerStrategy.Fire,
            // Foreign keys on the planned target tables are relaxed for the run and validated at the end, so
            // batch boundaries, same-table references and cycles cannot fail a transfer mid-way.
            ConstraintStrategy.DisableAndRevalidate,
            stableKeys
                .Select(pair => new StableKeyDefinition(
                    Address(pair.Key),
                    pair.Value.Constraint!.Name,
                    pair.Value.Constraint.Columns
                ))
                .ToArray(),
            tables,
            new BatchTarget(2000, 8 * 1024 * 1024),
            VerificationStrategy.StrictExact,
            totals
        );
    }

    /// <summary>
    /// Parents before children (Kahn's algorithm over the planned tables), so a target that enforces foreign keys
    /// accepts the rows in the order they are written. Tables in a cycle fall back to deepest-generation-first.
    /// </summary>
    private static IReadOnlyDictionary<TableDefinition, int> TransferOrder(
        IReadOnlyList<TableDefinition> planned,
        IReadOnlyCollection<ClosureRelationship> relationships,
        IReadOnlyDictionary<TableDefinition, int> depth
    )
    {
        var set = planned.ToHashSet();
        // child -> parents, restricted to planned tables and ignoring self references.
        var parents = planned.ToDictionary(
            table => table,
            table =>
                relationships
                    .Where(r => r.FromTable == table && r.ToTable != table && set.Contains(r.ToTable))
                    .Select(r => r.ToTable)
                    .ToHashSet()
        );
        var order = new Dictionary<TableDefinition, int>();
        var remaining = planned
            .OrderByDescending(table => depth[table])
            .ThenBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(table => parents[table].All(order.ContainsKey)) ?? remaining[0];
            order[next] = order.Count;
            remaining.Remove(next);
        }
        return order;
    }

    private static IReadOnlyCollection<ClosureRelationship> ReachableRelationships(
        IReadOnlyCollection<ForeignKeyDefinition> foreignKeys,
        TableDefinition root
    )
    {
        var tables = new HashSet<TableDefinition> { root };
        var frontier = new Queue<TableDefinition>([root]);
        var relationships = new List<ClosureRelationship>();
        while (frontier.TryDequeue(out var table))
            foreach (var foreignKey in foreignKeys.Where(foreignKey => foreignKey.ChildTable == table))
            {
                relationships.Add(new ClosureRelationship(foreignKey));
                if (tables.Add(foreignKey.ParentTable))
                    frontier.Enqueue(foreignKey.ParentTable);
            }
        return relationships;
    }

    private static UniqueConstraint StableKey(TableDefinition root, string name, IReadOnlyList<string> columns)
    {
        var key =
            root.PrimaryKey is { } primary && string.Equals(primary.Name, name, StringComparison.Ordinal)
                ? primary
                : root.UniqueConstraints.SingleOrDefault(unique =>
                    string.Equals(unique.Name, name, StringComparison.Ordinal)
                );
        return key is not null && key.Columns.SequenceEqual(columns, StringComparer.Ordinal)
            ? key
            : throw new InvalidOperationException("Selection stable key does not match the source catalog.");
    }

    private static (string Sql, IReadOnlyList<SelectionSqlParameter> Parameters, string ParameterHash) RawSql(
        string queryJson
    )
    {
        using var document = JsonDocument.Parse(queryJson);
        var query = document.RootElement;
        if (
            !query.TryGetProperty("Mode", out var mode)
            || !string.Equals(mode.GetString(), "raw", StringComparison.Ordinal)
            || !query.TryGetProperty("RawSql", out var raw)
            || string.IsNullOrWhiteSpace(raw.GetString())
            || !query.TryGetProperty("Parameters", out var values)
            || values.ValueKind != JsonValueKind.Array
        )
            throw new InvalidOperationException("Plan sealing supports saved raw SQL selections only.");
        var parameters = values.EnumerateArray().Select(SelectionParameters.FromJson).ToArray();
        return (
            raw.GetString()!,
            parameters,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(values.GetRawText())))
        );
    }

    private static TableAddress Address(TableDefinition table) => new(table.Schema, table.Name);
}
