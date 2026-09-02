namespace DataPitcher.Core.Schema;

public sealed record TableDefinition
{
    public TableDefinition(
        string schema,
        string name,
        IReadOnlyList<ColumnDefinition> columns,
        UniqueConstraint? primaryKey,
        IReadOnlyList<UniqueConstraint> uniqueConstraints)
    {
        Schema = schema;
        Name = name;
        Columns = Array.AsReadOnly(columns.ToArray());
        PrimaryKey = primaryKey;
        UniqueConstraints = Array.AsReadOnly(uniqueConstraints.ToArray());
    }

    public string Schema { get; }
    public string Name { get; }
    public IReadOnlyList<ColumnDefinition> Columns { get; }
    public UniqueConstraint? PrimaryKey { get; }
    public IReadOnlyList<UniqueConstraint> UniqueConstraints { get; }

    public bool Equals(TableDefinition? other) =>
        other is not null && Schema == other.Schema && Name == other.Name;

    public override int GetHashCode() => HashCode.Combine(Schema, Name);
}
