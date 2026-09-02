using System.Collections.ObjectModel;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Closure;

public enum RootConflictPolicy
{
    FailOnConflict,
    SkipExisting,
    Upsert,
}

public sealed class ClosureRelationship : IEquatable<ClosureRelationship>
{
    private ClosureRelationship(string name, TableDefinition fromTable, TableDefinition toTable, ForeignKeyDefinition? foreignKey, bool isInbound, bool isEnabled)
    {
        Name = name;
        FromTable = fromTable;
        ToTable = toTable;
        ForeignKey = foreignKey;
        IsInbound = isInbound;
        IsEnabled = isEnabled;
    }

    public ClosureRelationship(ForeignKeyDefinition foreignKey, bool isInbound = false, bool isEnabled = true)
        : this(foreignKey.Name, isInbound ? foreignKey.ParentTable : foreignKey.ChildTable, isInbound ? foreignKey.ChildTable : foreignKey.ParentTable, foreignKey, isInbound, isEnabled)
    {
    }

    public static ClosureRelationship Manual(string name, TableDefinition fromTable, TableDefinition toTable, bool isEnabled = true) =>
        new(name, fromTable, toTable, null, false, isEnabled);

    public string Name { get; }

    public TableDefinition FromTable { get; }

    public TableDefinition ToTable { get; }

    public ForeignKeyDefinition? ForeignKey { get; }

    public bool IsInbound { get; }

    public bool IsEnabled { get; }

    public bool Equals(ClosureRelationship? other) =>
        other is not null &&
        StringComparer.Ordinal.Equals(Name, other.Name) &&
        FromTable.Equals(other.FromTable) &&
        ToTable.Equals(other.ToTable) &&
        IsInbound == other.IsInbound &&
        IsEnabled == other.IsEnabled;

    public override bool Equals(object? obj) => Equals(obj as ClosureRelationship);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(FromTable);
        hash.Add(ToTable);
        hash.Add(IsInbound);
        hash.Add(IsEnabled);
        return hash.ToHashCode();
    }
}

public sealed class ClosureRoot
{
    public ClosureRoot(TableDefinition table, IEnumerable<StableKey> keys, RootConflictPolicy conflictPolicy)
    {
        Table = table;
        Keys = Array.AsReadOnly(keys.ToArray());
        ConflictPolicy = conflictPolicy;
    }

    public TableDefinition Table { get; }

    public IReadOnlyCollection<StableKey> Keys { get; }

    public RootConflictPolicy ConflictPolicy { get; }
}

public sealed class ClosureRequest
{
    public ClosureRequest(IEnumerable<ClosureRoot> roots, IEnumerable<ClosureRelationship> relationships, IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeySelections)
    {
        Roots = Array.AsReadOnly(roots.ToArray());
        Relationships = Array.AsReadOnly(relationships.ToArray());
        StableKeySelections = new ReadOnlyDictionary<TableDefinition, StableKeySelection>(stableKeySelections.ToDictionary(x => x.Key, x => x.Value));
    }

    public IReadOnlyCollection<ClosureRoot> Roots { get; }

    public IReadOnlyCollection<ClosureRelationship> Relationships { get; }

    public IReadOnlyDictionary<TableDefinition, StableKeySelection> StableKeySelections { get; }
}

public sealed record RowAddress(TableDefinition Table, StableKey Key);

public sealed record ClosureRow(TableDefinition Table, StableKey Key, int Generation);

public sealed record TargetConstraintState(string ConstraintName, bool IsPresent, bool IsEnforced, bool IsTrusted);

public sealed class TargetProbe
{
    public TargetProbe(bool exists, IReadOnlyDictionary<ClosureRelationship, TargetConstraintState> constraints)
    {
        Exists = exists;
        Constraints = new ReadOnlyDictionary<ClosureRelationship, TargetConstraintState>(constraints.ToDictionary(x => x.Key, x => x.Value));
    }

    public bool Exists { get; }

    public IReadOnlyDictionary<ClosureRelationship, TargetConstraintState> Constraints { get; }
}

public sealed record TargetConstraintWarning(string ConstraintName);

public sealed class ClosureResult
{
    public ClosureResult(IEnumerable<ClosureRow> rows, IEnumerable<TargetConstraintWarning> warnings)
    {
        Rows = Array.AsReadOnly(rows.ToArray());
        Warnings = Array.AsReadOnly(warnings.ToArray());
    }

    public IReadOnlyCollection<ClosureRow> Rows { get; }

    public IReadOnlyCollection<TargetConstraintWarning> Warnings { get; }

    public bool Contains(TableDefinition table, StableKey key) => Rows.Any(row => row.Table == table && row.Key == key);
}
