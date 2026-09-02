namespace DataPitcher.Core.Schema;

public sealed record UniqueConstraint(string Name, IReadOnlyList<string> Columns);
