using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataPitcher.Core.Closure;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Infrastructure.Connections;
using DataPitcher.Infrastructure.Plans;
using DataPitcher.Infrastructure.Schema;
using DataPitcher.Infrastructure.Selections;

namespace DataPitcher.Providers.SqlServer;

public sealed class PlanSealingService(
    PlanStore plans,
    SelectionStore selections,
    ConnectionProfileStore connections,
    SchemaSnapshotStore snapshots,
    ISecretReferenceResolver secrets
)
{
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
        if (
            !string.Equals(source.ProviderId, "sqlserver", StringComparison.Ordinal)
            || !string.Equals(target.ProviderId, "sqlserver", StringComparison.Ordinal)
        )
            throw new NotSupportedException(
                "Plan sealing currently requires SQL Server source and target connections."
            );
        var snapshot = await snapshots.GetAsync(sourceConnectionId, snapshotId, cancellationToken);
        var sourceConnection = await secrets.ResolveAsync(source.SecretReference, cancellationToken);
        var targetConnection = await secrets.ResolveAsync(target.SecretReference, cancellationToken);
        var sourceContent = await new SqlServerSchemaIntrospector().ReadAsync(
            source,
            sourceConnection,
            cancellationToken
        );
        if (!string.Equals(CanonicalSchemaSnapshotHasher.Hash(sourceContent), snapshot.Hash, StringComparison.Ordinal))
            throw new InvalidOperationException("Source schema changed since the selection snapshot.");
        var sourceCatalog = await new SqlServerCatalogReader(sourceConnection).ReadAsync(
            source.BusinessSchema,
            cancellationToken
        );
        var targetCatalog = await new SqlServerCatalogReader(targetConnection).ReadAsync(
            target.BusinessSchema,
            cancellationToken
        );
        var targetContent = await new SqlServerSchemaIntrospector().ReadAsync(
            target,
            targetConnection,
            cancellationToken
        );
        var root =
            sourceCatalog
                .Tables.SingleOrDefault(table =>
                    string.Equals(table.Definition.Schema, selection.RootSchema, StringComparison.Ordinal)
                    && string.Equals(table.Definition.Name, selection.RootTable, StringComparison.Ordinal)
                )
                ?.Definition
            ?? throw new InvalidOperationException("Selection root table was not found in the source catalog.");
        var rootKey = StableKey(root, selection.StableKeyConstraintName, selection.StableKeyColumns);
        var (rawSql, parameters, parameterHash) = RawSql(selection.QueryJson);
        var sql = new GeneratedSelectionSql(rawSql, root, rootKey, parameters, true);
        var executor = new SqlServerSelectionExecutor(sourceConnection, sourceCatalog);
        await executor.ValidateAsync(sql, cancellationToken);
        var seeds = await executor.ReadKeysAsync(
            sql,
            SelectionExecutionLimits.Default.MaximumResultSize,
            cancellationToken
        );
        var relationships = ReachableRelationships(sourceCatalog, root);
        var stableKeys = relationships
            .SelectMany(relationship => new[] { relationship.FromTable, relationship.ToTable })
            .Append(root)
            .Distinct()
            .ToDictionary(table => table, table => StableKeySelector.Select(table, null));
        stableKeys[root] = new StableKeySelection(rootKey);
        await using var store = new SqlServerClosureStore(
            sourceConnection,
            targetConnection,
            sourceCatalog,
            targetCatalog,
            stableKeys,
            planId,
            false
        );
        var closure = await new DependencyClosure(store).ComputeAsync(
            new ClosureRequest(
                [new ClosureRoot(root, seeds.Keys, RootConflictPolicy.FailOnConflict)],
                relationships,
                stableKeys
            ),
            cancellationToken
        );
        var content = Content(
            selection,
            snapshot,
            sourceContent,
            targetContent,
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
        DataPitcher.Core.Connections.ConnectionProfile source,
        DataPitcher.Core.Connections.ConnectionProfile target,
        TableDefinition root,
        IReadOnlyCollection<ClosureRelationship> relationships,
        IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
        ClosureResult closure,
        string parameterHash
    )
    {
        var tables = closure
            .Rows.GroupBy(row => row.Table)
            .OrderByDescending(group => group.Min(row => row.Generation))
            .ThenBy(group => group.Key.Schema, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Name, StringComparer.Ordinal)
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
                    DataPitcher.Core.Plans.RelationshipDirection.Outbound,
                    true
                ))
                .ToArray(),
            [new TableConflictPolicy(Address(root), RootConflictPolicy.FailOnConflict)],
            ConsistencyMode.FrozenKeys,
            TransferMode.ResumableStaged,
            TriggerStrategy.Fire,
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
            VerificationStrategy.StrictExact,
            totals
        );
    }

    private static IReadOnlyCollection<ClosureRelationship> ReachableRelationships(
        SqlServerSchemaSnapshot catalog,
        TableDefinition root
    )
    {
        var tables = new HashSet<TableDefinition> { root };
        var frontier = new Queue<TableDefinition>([root]);
        var relationships = new List<ClosureRelationship>();
        while (frontier.TryDequeue(out var table))
            foreach (var foreignKey in catalog.ForeignKeys.Where(foreignKey => foreignKey.ChildTable == table))
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
        var parameters = values.EnumerateArray().Select(Parameter).ToArray();
        return (
            raw.GetString()!,
            parameters,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(values.GetRawText())))
        );
    }

    private static SelectionSqlParameter Parameter(JsonElement parameter)
    {
        var name =
            parameter.GetProperty("Name").GetString()
            ?? throw new InvalidOperationException("Selection parameter name is required.");
        var value = parameter.GetProperty("Value");
        return parameter.GetProperty("Kind").GetString() switch
        {
            "int" => new SelectionSqlParameter(name, typeof(int), value.GetInt32()),
            "decimal" => new SelectionSqlParameter(name, typeof(decimal), value.GetDecimal()),
            "boolean" => new SelectionSqlParameter(name, typeof(bool), value.GetBoolean()),
            "date" => new SelectionSqlParameter(name, typeof(DateOnly), DateOnly.Parse(value.GetString()!)),
            "time" => new SelectionSqlParameter(name, typeof(TimeOnly), TimeOnly.Parse(value.GetString()!)),
            "dateTime" => new SelectionSqlParameter(name, typeof(DateTime), value.GetDateTime()),
            "guid" => new SelectionSqlParameter(name, typeof(Guid), value.GetGuid()),
            "string" => new SelectionSqlParameter(
                name,
                typeof(string),
                value.GetString() ?? throw new InvalidOperationException("Selection parameter value is required.")
            ),
            _ => throw new InvalidOperationException("Selection parameter kind is not supported."),
        };
    }

    private static TableAddress Address(TableDefinition table) => new(table.Schema, table.Name);
}
