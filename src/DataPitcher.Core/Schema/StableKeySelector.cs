namespace DataPitcher.Core.Schema;

public sealed record StableKeySelection(UniqueConstraint? Constraint)
{
    public bool HasStableKey => Constraint is not null;
    public static StableKeySelection NoStableKey { get; } = new((UniqueConstraint?)null);
}

public static class StableKeySelector
{
    public static StableKeySelection Select(TableDefinition table, string? selectedUniqueConstraint)
    {
        if (table.PrimaryKey is not null)
            return new(table.PrimaryKey);

        var unique = table.UniqueConstraints.FirstOrDefault(x =>
            StringComparer.Ordinal.Equals(x.Name, selectedUniqueConstraint));
        return unique is not null && unique.Columns.All(column =>
            table.Columns.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.Name, column)) is { IsNullable: false })
            ? new(unique)
            : StableKeySelection.NoStableKey;
    }
}
