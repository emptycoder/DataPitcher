namespace DataPitcher.Core.Schema;

public sealed record TableDefinition(
    string Schema,
    string Name,
    IReadOnlyList<ColumnDefinition> Columns,
    UniqueConstraint? PrimaryKey,
    IReadOnlyList<UniqueConstraint> UniqueConstraints)
{
    public bool Equals(TableDefinition? other) =>
        other is not null && Schema == other.Schema && Name == other.Name;

    public override int GetHashCode() => HashCode.Combine(Schema, Name);
}
