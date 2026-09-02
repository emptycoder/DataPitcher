namespace DataPitcher.Core.Schema;

public sealed record StableKeySelection(UniqueConstraint? Constraint)
{
    public bool HasStableKey => Constraint is not null;
    public static StableKeySelection NoStableKey { get; } = new((UniqueConstraint?)null);
}

public static class StableKeySelector
{
    public static StableKeySelection Select(TableDefinition table, string? selectedUniqueConstraint) =>
        table.PrimaryKey is not null
            ? new(table.PrimaryKey)
            : table.UniqueConstraints.FirstOrDefault(x =>
                StringComparer.Ordinal.Equals(x.Name, selectedUniqueConstraint)) is { } unique
                ? new(unique)
                : StableKeySelection.NoStableKey;
}
