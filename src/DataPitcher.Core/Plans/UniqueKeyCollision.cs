namespace DataPitcher.Core.Plans;

/// <summary>
/// Planned rows whose value on a unique key of the target (other than the stable key) belongs to a different target
/// row. DataPitcher writes keys verbatim, so such a row can neither be inserted nor safely skipped: its children
/// would point at a key the target never gets. <paramref name="Samples"/> pairs the source key with the target key.
/// </summary>
public sealed record UniqueKeyCollision(
    TableAddress Table,
    IReadOnlyList<string> Columns,
    long Rows,
    IReadOnlyList<string> Samples
);
