namespace DataPitcher.Core.Schema;

/// <summary>
/// How DataPitcher matches schema, table, column, constraint and key-component names between what the operator
/// typed, what one catalog declared and what another declared. Both supported engines resolve unquoted
/// identifiers without regard to case, and a target is routinely created in a different case than its source, so
/// names match case-insensitively; SQL is still emitted with each side's own catalog casing.
/// </summary>
public static class DatabaseNames
{
    public static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    public static bool Equals(string? left, string? right) => Comparer.Equals(left, right);
}
