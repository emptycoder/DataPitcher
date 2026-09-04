using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Plans;

/// <summary>One source column's target, chosen by the operator; a null target means the column is not transferred.</summary>
public sealed record ColumnMappingOverride(string Source, string? Target);

/// <summary>The operator's choices for one source table: a different target table and per-column targets.</summary>
public sealed record TableMappingOverride(
    TableAddress Source,
    TableAddress? Target,
    IReadOnlyList<ColumnMappingOverride> Columns
);

public sealed record MappingProblem(string Code, string Message, bool IsBlocker);

/// <summary>Where a column's mapping came from: matched by name, chosen by the operator, excluded by the operator, or found nothing.</summary>
public static class MappingOrigins
{
    public const string Default = "default";
    public const string Override = "override";
    public const string Excluded = "excluded";
    public const string Unmapped = "unmapped";
}

public sealed record ColumnMappingReview(
    string Source,
    string SourceType,
    bool SourceNullable,
    string? Target,
    string? TargetType,
    bool? TargetNullable,
    bool IsKey,
    bool IsForeignKey,
    string Origin,
    IReadOnlyList<MappingProblem> Problems
);

public sealed record TargetColumnReview(
    string Name,
    string Type,
    bool IsNullable,
    IReadOnlyList<MappingProblem> Problems
);

public sealed record TableMappingReview(
    TableAddress Source,
    TableAddress Target,
    bool TargetExists,
    bool IsRoot,
    IReadOnlyList<ColumnMappingReview> Columns,
    IReadOnlyList<TargetColumnReview> TargetOnlyColumns,
    IReadOnlyList<MappingProblem> Problems
)
{
    public IEnumerable<MappingProblem> AllProblems =>
        Problems
            .Concat(Columns.SelectMany(column => column.Problems))
            .Concat(TargetOnlyColumns.SelectMany(column => column.Problems));

    /// <summary>The mapping the transfer writes with: only columns that have a target.</summary>
    public TableMapping ToMapping() =>
        new(
            Source,
            Target,
            Columns
                .Where(column => column.Target is not null)
                .Select(column => new ColumnMapping(column.Source, column.Target!))
                .ToArray()
        );
}

public sealed class PlanMappingReview
{
    public PlanMappingReview(IEnumerable<TableMappingReview> tables, IEnumerable<MappingProblem> problems)
    {
        Tables = Array.AsReadOnly(tables.ToArray());
        Problems = Array.AsReadOnly(problems.ToArray());
    }

    public IReadOnlyList<TableMappingReview> Tables { get; }

    /// <summary>Problems that belong to the plan as a whole rather than to one table.</summary>
    public IReadOnlyList<MappingProblem> Problems { get; }

    public IEnumerable<MappingProblem> AllProblems => Problems.Concat(Tables.SelectMany(table => table.AllProblems));

    public bool HasBlockers => AllProblems.Any(problem => problem.IsBlocker);

    public IEnumerable<PlanWarning> Warnings =>
        AllProblems
            .Where(problem => !problem.IsBlocker)
            .Select(problem => new PlanWarning(problem.Code, problem.Message));

    public TableMappingReview Table(TableAddress source) =>
        Tables.Single(table =>
            DatabaseNames.Equals(table.Source.Schema, source.Schema)
            && DatabaseNames.Equals(table.Source.Name, source.Name)
        );
}

/// <summary>
/// Prefills every reachable table's column mapping by name, applies the operator's overrides on top, and says what
/// the target cannot take: a missing table or column, a key or foreign-key column with nowhere to go, two columns
/// aimed at one target column. The rest are warnings the operator should see before the transfer, not after.
/// </summary>
public static class PlanMappingResolver
{
    public static PlanMappingReview Resolve(
        SchemaSnapshotContent source,
        SchemaSnapshotContent? target,
        SchemaTableAddress root,
        IReadOnlyList<string> rootKeyColumns,
        IReadOnlyList<TableMappingOverride> overrides
    )
    {
        var problems = new List<MappingProblem>();
        if (target is null)
            problems.Add(
                new MappingProblem(
                    "target_snapshot_missing",
                    "The target connection has no schema snapshot, so the column mapping is prefilled by name only and cannot be checked against the target until the target is scanned.",
                    false
                )
            );
        var tables = Reachable(source, root)
            .Select(table =>
                ResolveTable(
                    source,
                    target,
                    table,
                    Same(table, root) ? rootKeyColumns : table.PrimaryKey?.Columns ?? [],
                    Same(table, root),
                    overrides
                )
            )
            .ToArray();
        return new PlanMappingReview(tables, problems);
    }

    private static TableMappingReview ResolveTable(
        SchemaSnapshotContent source,
        SchemaSnapshotContent? target,
        SchemaTable table,
        IReadOnlyList<string> keyColumns,
        bool isRoot,
        IReadOnlyList<TableMappingOverride> overrides
    )
    {
        var address = new TableAddress(table.Schema, table.Name);
        var chosen = overrides.FirstOrDefault(candidate => Same(candidate.Source, address));
        var targetAddress = chosen?.Target ?? address;
        var targetTable = target?.Tables.FirstOrDefault(candidate =>
            DatabaseNames.Equals(candidate.Schema, targetAddress.Schema)
            && DatabaseNames.Equals(candidate.Name, targetAddress.Name)
        );
        var resolvedAddress = targetTable is null
            ? targetAddress
            : new TableAddress(targetTable.Schema, targetTable.Name);
        var foreignKeyColumns = source
            .ForeignKeys.Where(foreignKey => Same(table, foreignKey.ChildTable))
            .SelectMany(foreignKey => foreignKey.ChildColumns)
            .Concat(
                source
                    .ForeignKeys.Where(foreignKey => Same(table, foreignKey.ParentTable))
                    .SelectMany(foreignKey => foreignKey.ParentColumns)
            )
            .ToHashSet(DatabaseNames.Comparer);
        var keys = keyColumns.ToHashSet(DatabaseNames.Comparer);
        var columns = table
            .Columns.Select(column =>
                ResolveColumn(
                    address,
                    column,
                    targetTable,
                    chosen,
                    keys.Contains(column.Name),
                    foreignKeyColumns.Contains(column.Name)
                )
            )
            .ToList();
        var duplicates = columns
            .Where(column => column.Target is not null)
            .GroupBy(column => column.Target!, DatabaseNames.Comparer)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(DatabaseNames.Comparer);
        columns = columns
            .Select(column =>
                column.Target is not null && duplicates.Contains(column.Target)
                    ? column with
                    {
                        Problems = column
                            .Problems.Append(
                                new MappingProblem(
                                    "duplicate_target",
                                    $"{Name(address)}: columns {string.Join(", ", columns.Where(other => DatabaseNames.Equals(other.Target, column.Target)).Select(other => other.Source))} are all mapped to target column {column.Target}; a target column can take one source column.",
                                    true
                                )
                            )
                            .ToArray(),
                    }
                    : column
            )
            .ToList();
        var mappedTargets = columns
            .Where(column => column.Target is not null)
            .Select(column => column.Target!)
            .ToHashSet(DatabaseNames.Comparer);
        var targetOnly = (targetTable?.Columns ?? [])
            .Where(column => !mappedTargets.Contains(column.Name))
            .Select(column => new TargetColumnReview(
                column.Name,
                column.StoreType,
                column.IsNullable,
                column.IsNullable
                    ? []
                    :
                    [
                        new MappingProblem(
                            "target_required_unfilled",
                            $"{Name(resolvedAddress)}: target column {column.Name} is NOT NULL and receives no source column; inserts fail unless the target supplies a default or generates it.",
                            false
                        ),
                    ]
            ))
            .ToArray();
        var tableProblems = new List<MappingProblem>();
        // The target snapshot covers one business schema, so a table missing from it may still exist; the mapping is
        // prefilled by name and left unchecked, and the transfer fails on the target if the table is really absent.
        if (target is not null && targetTable is null)
            tableProblems.Add(
                new MappingProblem(
                    "target_table_missing",
                    $"{Name(address)}: the target's schema snapshot has no table {Name(targetAddress)}, so its columns are mapped by name unchecked. Scan the target's schema, create the table, or map the table to another target table.",
                    false
                )
            );
        return new TableMappingReview(
            address,
            resolvedAddress,
            targetTable is not null,
            isRoot,
            columns,
            targetOnly,
            tableProblems
        );
    }

    private static ColumnMappingReview ResolveColumn(
        TableAddress address,
        SchemaColumn column,
        SchemaTable? targetTable,
        TableMappingOverride? chosen,
        bool isKey,
        bool isForeignKey
    )
    {
        var unverified = targetTable is null;
        var choice = chosen?.Columns.FirstOrDefault(candidate => DatabaseNames.Equals(candidate.Source, column.Name));
        var problems = new List<MappingProblem>();
        string? targetName;
        string origin;
        if (choice is { Target: null })
        {
            targetName = null;
            origin = MappingOrigins.Excluded;
        }
        else if (choice is not null)
        {
            targetName = choice.Target;
            origin = MappingOrigins.Override;
        }
        else
        {
            targetName =
                unverified || targetTable is null
                    ? column.Name
                    : targetTable
                        .Columns.FirstOrDefault(candidate => DatabaseNames.Equals(candidate.Name, column.Name))
                        ?.Name;
            origin = targetName is null ? MappingOrigins.Unmapped : MappingOrigins.Default;
        }
        var targetColumn =
            targetName is null || targetTable is null
                ? null
                : targetTable.Columns.FirstOrDefault(candidate => DatabaseNames.Equals(candidate.Name, targetName));
        if (targetColumn is not null)
            targetName = targetColumn.Name;
        var table = Name(address) + "." + column.Name;
        if (origin == MappingOrigins.Override && targetTable is not null && targetColumn is null)
            problems.Add(
                new MappingProblem(
                    "target_column_missing",
                    $"{table} is mapped to target column {targetName}, which the target table does not have.",
                    true
                )
            );
        if (targetName is null && isKey)
            problems.Add(
                new MappingProblem(
                    "key_column_unmapped",
                    $"{table} is part of the stable key and must be transferred; map it to a target column.",
                    true
                )
            );
        else if (targetName is null && isForeignKey)
            problems.Add(
                new MappingProblem(
                    "foreign_key_column_unmapped",
                    $"{table} carries a foreign key the transfer follows and must be transferred; map it to a target column.",
                    true
                )
            );
        else if (origin == MappingOrigins.Unmapped)
            problems.Add(
                new MappingProblem(
                    "column_unmapped",
                    $"{table} has no target column of the same name; its values are not transferred unless you map it.",
                    false
                )
            );
        if (targetColumn is not null && !string.Equals(column.ClrType, targetColumn.ClrType, StringComparison.Ordinal))
            problems.Add(
                new MappingProblem(
                    "type_mismatch",
                    $"{table} ({column.StoreType}) is mapped to {targetColumn.Name} ({targetColumn.StoreType}); the values are converted by the target and may be rejected.",
                    false
                )
            );
        if (targetColumn is not null && column.IsNullable && !targetColumn.IsNullable)
            problems.Add(
                new MappingProblem(
                    "nullability_narrowed",
                    $"{table} allows NULL but {targetColumn.Name} does not; a NULL value fails the insert.",
                    false
                )
            );
        return new ColumnMappingReview(
            column.Name,
            column.StoreType,
            column.IsNullable,
            targetName,
            targetColumn?.StoreType,
            targetColumn?.IsNullable,
            isKey,
            isForeignKey,
            origin,
            problems
        );
    }

    /// <summary>The root and every table its foreign keys lead to, the same walk sealing follows.</summary>
    private static IEnumerable<SchemaTable> Reachable(SchemaSnapshotContent source, SchemaTableAddress root)
    {
        var start = source.Tables.FirstOrDefault(table => Same(table, root));
        if (start is null)
            yield break;
        var seen = new HashSet<SchemaTable> { start };
        var frontier = new Queue<SchemaTable>([start]);
        while (frontier.TryDequeue(out var table))
        {
            yield return table;
            foreach (var foreignKey in source.ForeignKeys.Where(foreignKey => Same(table, foreignKey.ChildTable)))
            {
                var parent = source.Tables.FirstOrDefault(candidate => Same(candidate, foreignKey.ParentTable));
                if (parent is not null && seen.Add(parent))
                    frontier.Enqueue(parent);
            }
        }
    }

    private static bool Same(SchemaTable table, SchemaTableAddress address) =>
        DatabaseNames.Equals(table.Schema, address.Schema) && DatabaseNames.Equals(table.Name, address.Name);

    private static bool Same(TableAddress left, TableAddress right) =>
        DatabaseNames.Equals(left.Schema, right.Schema) && DatabaseNames.Equals(left.Name, right.Name);

    private static string Name(TableAddress address) => address.Schema + "." + address.Name;
}
