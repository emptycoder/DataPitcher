namespace DataPitcher.Core.Schema;

/// <summary>
/// A foreign key whose parent table the catalog could not load, typically because the login cannot see it. The
/// graph has no edge for it while the database still enforces it, so a plan over the child table is incomplete.
/// </summary>
public sealed record UnresolvedForeignKey(string Name, SchemaTableAddress ChildTable, SchemaTableAddress ParentTable);
