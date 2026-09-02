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
        if (childColumns.Count != parentColumns.Count)
            throw new ArgumentException("Foreign-key child and parent column counts must match.");

        Name = name;
        ChildTable = childTable;
        ParentTable = parentTable;
        ChildColumns = childColumns;
        ParentColumns = parentColumns;
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
}
