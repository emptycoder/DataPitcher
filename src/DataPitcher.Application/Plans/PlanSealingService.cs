using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataPitcher.Application.Connections;
using DataPitcher.Application.Schema;
using DataPitcher.Application.Selection;
using DataPitcher.Core.Closure;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Application.Plans;

/// <summary>A planned table has a foreign key the source login cannot follow; the message names it.</summary>
public sealed class IncompleteGraphException(string message) : InvalidOperationException(message);

/// <summary>Planned rows reference source parents that do not exist while the target enforces the constraint.</summary>
public sealed class SourceOrphansException(string message) : InvalidOperationException(message);

/// <summary>Planned rows collide with different target rows on a unique key; verbatim keys cannot merge them.</summary>
public sealed class UniqueKeyCollisionException(string message) : InvalidOperationException(message);

/// <summary>A transfer of this plan is still running or paused; sealing again would pull its sealed keys away.</summary>
public sealed class PlanInUseException(string message) : InvalidOperationException(message);

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
    IEnumerable<ISealingProvider> providers,
    IJobRepository jobs
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
        RequireNoActiveTransfer(planId, cancellationToken);
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
        // The plan is sealed against exactly the snapshot the selection was saved with: the selection's SQL and root
        // key were validated against it. When the source has changed since, the operator re-points the selection.
        if (
            !string.Equals(
                CanonicalSchemaSnapshotHasher.Hash(session.SourceSchema),
                snapshot.Hash,
                StringComparison.Ordinal
            )
        )
            throw new InvalidOperationException(
                $"The source schema changed after the selection's schema snapshot was captured ({snapshot.CapturedAtUtc:u}). Scan the source connection from Connections if it has no scan of the current schema yet, then open the selection in the Selection Workbench, choose the current snapshot under \"Schema snapshot\", save the selection, and seal the plan again."
            );
        var root =
            session.SourceTables.SingleOrDefault(table =>
                DatabaseNames.Equals(table.Schema, selection.RootSchema)
                && DatabaseNames.Equals(table.Name, selection.RootTable)
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
        RequireCompleteGraph(session.SourceUnresolvedForeignKeys, relationships, root);
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
            // A plan sealed again must not see its own earlier keys as already discovered.
            await store.ResetAsync(cancellationToken);
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
        var depth = closure
            .Rows.GroupBy(row => row.Table)
            .ToDictionary(group => group.Key, group => group.Max(row => row.Generation));
        var order = ImportOrdering.Plan(
            depth.Keys.ToArray(),
            relationships,
            depth,
            relationship => IsNullable(relationship, session.TargetSchema)
        );
        if (order.Levelled.Count > 0)
            await session.OrderHierarchiesAsync(order.Levelled, stableKeys, planId, cancellationToken);
        RequireNoUniqueKeyCollisions(
            await session.FindUniqueKeyCollisionsAsync(depth.Keys.ToArray(), stableKeys, planId, cancellationToken)
        );
        var warnings = new List<PlanWarning>();
        // An empty or shrunken plan is legitimate, but the operator has to be told why the rows are not coming.
        if (seeds.Keys.Count == 0)
            warnings.Add(
                new PlanWarning("selection_empty", "The selection returned no rows, so there is nothing to transfer.")
            );
        if (closure.SkippedRoots > 0)
            warnings.Add(
                new PlanWarning(
                    "roots_skipped",
                    $"{closure.SkippedRoots} of {seeds.Keys.Count} selected row(s) already exist in the target and are skipped, together with their dependencies (conflict policy SkipExisting); for example {Describe(closure.SkippedRootSamples)}."
                        + (
                            closure.Rows.Count == 0
                                ? " Nothing is left to transfer: delete those rows from the target or select other rows."
                                : ""
                        )
                )
            );
        warnings.AddRange(
            closure.Warnings.Select(warning => new PlanWarning(
                "target_constraint_untrusted",
                $"Target constraint {warning.ConstraintName} is missing, disabled or untrusted, so rows behind it are transferred instead of trusted to exist."
            ))
        );
        warnings.AddRange(Orphans(closure.Orphans, relationships, session.TargetSchema));
        var verification = VerificationStrategy.StrictExact;
        var blockers = await session.VerificationBlockersAsync(depth.Keys.Select(Address).ToArray(), cancellationToken);
        if (blockers.Count > 0)
        {
            // Never claim a guarantee the target cannot record: exact-set verification needs side-effect-free tables.
            verification = VerificationStrategy.Standard;
            warnings.AddRange(
                blockers.Select(blocker => new PlanWarning(
                    "verification_downgraded",
                    blocker
                        + " Exact-set verification is not available for this plan; row counts and foreign keys are still verified."
                ))
            );
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
            order,
            parameterHash,
            verification,
            warnings
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
        ImportOrder order,
        string parameterHash,
        VerificationStrategy verification,
        IReadOnlyList<PlanWarning> warnings
    )
    {
        var groups = closure.Rows.GroupBy(row => row.Table).ToArray();
        var tables = groups
            .OrderBy(group => order.Order[group.Key])
            .Select(group =>
            {
                var address = Address(group.Key);
                var count = group.LongCount();
                var deferred = order
                    .Deferred.Where(relationship => relationship.FromTable == group.Key)
                    .SelectMany(relationship => relationship.FromColumns)
                    .Distinct(DatabaseNames.Comparer)
                    .ToArray();
                // A nullable levelled self reference can also be held back for the rows the levelling cannot reach.
                var hierarchy = order
                    .Levelled.Where(relationship =>
                        relationship.FromTable == group.Key && IsNullable(relationship, targetSchema)
                    )
                    .SelectMany(relationship => relationship.FromColumns)
                    .Distinct(DatabaseNames.Comparer)
                    .ToArray();
                return new PlanTable(
                    new TableMapping(
                        address,
                        address,
                        group.Key.Columns.Select(column => new ColumnMapping(column.Name, column.Name)).ToArray()
                    ),
                    group.Key == root ? PlanTableState.Root : PlanTableState.RequiredDependency,
                    new ManifestCounts(count, count, count, 0),
                    new TopologicalGroup([address]),
                    deferred.Length > 0 ? CycleStrategy.NullableForeignKeyTwoPhase : CycleStrategy.NotApplicable,
                    deferred,
                    hierarchy
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
            // Constraints stay enforced: the import order comes from the table graph (parents first across tables,
            // ancestors first within a table) so every row's parents are already in the target when it arrives.
            ConstraintStrategy.Enforce,
            stableKeys
                .Select(pair => new StableKeyDefinition(
                    Address(pair.Key),
                    pair.Value.Constraint!.Name,
                    pair.Value.Constraint.Columns
                ))
                .ToArray(),
            tables,
            new BatchTarget(2000, 8 * 1024 * 1024),
            verification,
            totals,
            TransferPlanContent.CurrentSealingVersion,
            warnings
        );
    }

    /// <summary>
    /// Keys are written verbatim, so a planned row whose unique value is taken by a different target row has no
    /// correct outcome: inserting violates the key, skipping leaves its children pointing at a key the target never
    /// gets, and merging would need key remapping. Refuse before any batch is written.
    /// </summary>
    private static void RequireNoUniqueKeyCollisions(IReadOnlyCollection<UniqueKeyCollision> collisions)
    {
        if (collisions.Count == 0)
            return;
        throw new UniqueKeyCollisionException(
            "Planned rows collide with different rows already in the target: "
                + string.Join(
                    "; ",
                    collisions.Select(collision =>
                        $"{collision.Rows} row(s) of {collision.Table.Schema}.{collision.Table.Name} on unique key ({string.Join(", ", collision.Columns)}), for example {string.Join(", ", collision.Samples)}"
                    )
                )
                + ". DataPitcher writes keys verbatim and cannot merge two rows into one. Remove or re-key the target rows, or exclude the source rows from the selection, then seal the plan again."
        );
    }

    /// <summary>
    /// Sealing replaces the plan content and rebuilds the sealed key set the transfer reads, so it must not happen
    /// underneath a job that is still queued, running or paused; the operator finishes or cancels that job first.
    /// </summary>
    private void RequireNoActiveTransfer(Guid planId, CancellationToken cancellationToken)
    {
        var active = jobs.List(cancellationToken)
            .FirstOrDefault(job =>
                job.PlanId == planId
                && job.State
                    is not (JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.VerificationFailed)
            );
        if (active is not null)
            throw new PlanInUseException(
                $"Transfer job {active.JobId} for this plan is {active.State}. Sealing again would replace the sealed rows that job reads; wait for it to finish or cancel it, then seal the plan."
            );
    }

    /// <summary>
    /// A planned table whose foreign key points at a table the login cannot see has an edge the graph does not
    /// know about while the target still enforces it, so the closure cannot be complete. Refuse rather than guess.
    /// </summary>
    private static void RequireCompleteGraph(
        IReadOnlyCollection<UnresolvedForeignKey> unresolved,
        IReadOnlyCollection<ClosureRelationship> relationships,
        TableDefinition root
    )
    {
        var planned = relationships
            .SelectMany(relationship => new[] { relationship.FromTable, relationship.ToTable })
            .Append(root)
            .Select(table => new SchemaTableAddress(table.Schema, table.Name))
            .ToHashSet();
        var missing = unresolved.Where(foreignKey => planned.Contains(foreignKey.ChildTable)).ToArray();
        if (missing.Length == 0)
            return;
        throw new IncompleteGraphException(
            "The dependency graph is incomplete: "
                + string.Join(
                    "; ",
                    missing.Select(foreignKey =>
                        $"{foreignKey.Name} on {foreignKey.ChildTable.Schema}.{foreignKey.ChildTable.Name} references {foreignKey.ParentTable.Schema}.{foreignKey.ParentTable.Name}, which the source login cannot read"
                    )
                )
                + ". Grant the source login SELECT on the referenced table(s), rescan the schema and seal again; otherwise the target would reject rows whose parents were never planned."
        );
    }

    /// <summary>
    /// Rows whose foreign key resolves to no source parent are transferred as they are (the source has no parent to
    /// fabricate). When the target enforces the constraint they would fail mid-run, so sealing refuses; otherwise
    /// the plan carries a warning.
    /// </summary>
    private static string Describe(IEnumerable<StableKey> keys) =>
        string.Join(
            "; ",
            keys.Select(key =>
                string.Join(", ", key.Components.Select(component => $"{component.Column}={component.Value}"))
            )
        );

    private static IEnumerable<PlanWarning> Orphans(
        IReadOnlyCollection<SourceOrphanWarning> orphans,
        IReadOnlyCollection<ClosureRelationship> relationships,
        SchemaSnapshotContent targetSchema
    )
    {
        var enforced = new List<string>();
        var warnings = new List<PlanWarning>();
        foreach (var orphan in orphans)
        {
            var relationship = relationships.First(candidate =>
                DatabaseNames.Equals(candidate.Name, orphan.RelationshipName)
            );
            var where =
                $"{orphan.Rows} planned row(s) in {relationship.FromTable.Schema}.{relationship.FromTable.Name} reference a {relationship.ToTable.Schema}.{relationship.ToTable.Name} row through {relationship.Name} that does not exist in the source";
            if (IsEnforcedOnTarget(relationship, targetSchema))
                enforced.Add(where);
            else
                warnings.Add(
                    new PlanWarning(
                        "source_orphans",
                        where + "; the target does not enforce the constraint, so the rows are transferred as they are."
                    )
                );
        }
        if (enforced.Count > 0)
            throw new SourceOrphansException(
                string.Join("; ", enforced)
                    + ". The target enforces the constraint and would reject these rows mid-run. Repair the source rows or disable the constraint on the target before sealing."
            );
        return warnings;
    }

    private static bool IsEnforcedOnTarget(ClosureRelationship relationship, SchemaSnapshotContent targetSchema) =>
        targetSchema.ForeignKeys.Any(foreignKey =>
            foreignKey.IsEnforced
            && DatabaseNames.Equals(foreignKey.ChildTable.Schema, relationship.FromTable.Schema)
            && DatabaseNames.Equals(foreignKey.ChildTable.Name, relationship.FromTable.Name)
            && foreignKey.ChildColumns.SequenceEqual(relationship.FromColumns, DatabaseNames.Comparer)
            && foreignKey.ParentColumns.SequenceEqual(relationship.ToColumns, DatabaseNames.Comparer)
        );

    /// <summary>A relationship can be deferred when the target accepts NULL in every referencing column.</summary>
    private static bool IsNullable(ClosureRelationship relationship, SchemaSnapshotContent targetSchema)
    {
        var table = targetSchema.Tables.SingleOrDefault(candidate =>
            DatabaseNames.Equals(candidate.Schema, relationship.FromTable.Schema)
            && DatabaseNames.Equals(candidate.Name, relationship.FromTable.Name)
        );
        return relationship.FromColumns.All(name =>
            table is null
                ? relationship.FromTable.Columns.Any(column =>
                    DatabaseNames.Equals(column.Name, name) && column.IsNullable
                )
                : table.Columns.Any(column => DatabaseNames.Equals(column.Name, name) && column.IsNullable)
        );
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
            root.PrimaryKey is { } primary && DatabaseNames.Equals(primary.Name, name)
                ? primary
                : root.UniqueConstraints.SingleOrDefault(unique => DatabaseNames.Equals(unique.Name, name));
        // The catalog's own spelling of the key columns is what every later query uses.
        return key is not null && key.Columns.SequenceEqual(columns, DatabaseNames.Comparer)
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
