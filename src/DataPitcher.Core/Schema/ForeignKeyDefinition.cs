namespace DataPitcher.Core.Schema;

public sealed class ForeignKeyDefinition
{
    public ForeignKeyDefinition(
        string name,
        TableDefinition childTable,
        TableDefinition parentTable,
        IReadOnlyList<string> childColumns,
        IReadOnlyList<string> parentColumns,
        bool isEnforced,
        bool isTrusted)
    {
        if (childColumns.Count == 0 || parentColumns.Count == 0)
            throw new ArgumentException("Foreign-key child and parent column lists must not be empty.");

        if (childColumns.Count != parentColumns.Count)
            throw new ArgumentException("Foreign-key child and parent column counts must match.");

        Name = name;
        ChildTable = childTable;
        ParentTable = parentTable;
        ChildColumns = Array.AsReadOnly(childColumns.ToArray());
        ParentColumns = Array.AsReadOnly(parentColumns.ToArray());
        IsEnforced = isEnforced;
        IsTrusted = isTrusted;
    }

    public string Name { get; }
    public TableDefinition ChildTable { get; }
    public TableDefinition ParentTable { get; }
    public IReadOnlyList<string> ChildColumns { get; }
    public IReadOnlyList<string> ParentColumns { get; }
    public bool IsEnforced { get; }
    public bool IsTrusted { get; }

    public override bool Equals(object? obj) =>
        obj is ForeignKeyDefinition other &&
        Name == other.Name &&
        ChildTable.Equals(other.ChildTable) &&
        ParentTable.Equals(other.ParentTable) &&
        ChildColumns.SequenceEqual(other.ChildColumns) &&
        ParentColumns.SequenceEqual(other.ParentColumns) &&
        IsEnforced == other.IsEnforced &&
        IsTrusted == other.IsTrusted;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(ChildTable);
        hash.Add(ParentTable);
        foreach (var column in ChildColumns)
            hash.Add(column);
        foreach (var column in ParentColumns)
            hash.Add(column);
        hash.Add(IsEnforced);
        hash.Add(IsTrusted);
        return hash.ToHashCode();
    }
}
