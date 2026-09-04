using DataPitcher.Core.Closure;
using DataPitcher.Core.Schema;

namespace DataPitcher.Application.Plans;

/// <summary>
/// The order tables are written in, the self references levelled through the sealed keys, and the relationships
/// whose columns are filled in after every table has been written.
/// </summary>
public sealed record ImportOrder(
    IReadOnlyDictionary<TableDefinition, int> Order,
    IReadOnlyCollection<ClosureRelationship> Levelled,
    IReadOnlyCollection<ClosureRelationship> Deferred
);

/// <summary>No write order satisfies the plan's foreign keys with constraints enforced; the message names the cycle.</summary>
public sealed class UnorderablePlanException(string message) : InvalidOperationException(message);

/// <summary>
/// Derives the import order from the table graph so that foreign keys stay enforced on the target: parents before
/// children (Kahn's algorithm), one self reference per table levelled through the sealed keys, and cycles broken at
/// nullable foreign keys whose values are written in a second pass. A cycle without a nullable edge has no valid
/// order, and sealing refuses it rather than writing a child before its parent.
/// </summary>
public static class ImportOrdering
{
    public static ImportOrder Plan(
        IReadOnlyList<TableDefinition> planned,
        IReadOnlyCollection<ClosureRelationship> relationships,
        IReadOnlyDictionary<TableDefinition, int> depth,
        Func<ClosureRelationship, bool> deferrable
    )
    {
        var set = planned.ToHashSet();
        var levelled = new List<ClosureRelationship>();
        var deferred = new List<ClosureRelationship>();
        foreach (var table in planned)
        {
            var self = relationships
                .Where(r => r.FromTable == table && r.ToTable == table)
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .ToArray();
            if (self.Length == 0)
                continue;
            // One self reference is levelled through the sealed keys (the non-nullable one when there is one, since
            // it cannot be deferred); every other one is written in the second phase, so it must be nullable.
            var level = self.FirstOrDefault(r => !deferrable(r)) ?? self[0];
            levelled.Add(level);
            foreach (var relationship in self.Where(r => r != level))
                if (deferrable(relationship))
                    deferred.Add(relationship);
                else
                    throw new UnorderablePlanException(
                        $"{Name(table)} references itself through both {level.Name} and {relationship.Name} ({Columns(relationship)}) with columns that are not nullable in the target; only one non-nullable self reference per table can be ordered. Make {Columns(relationship)} nullable in the target or exclude the table."
                    );
        }
        var cross = relationships
            .Where(r => r.FromTable != r.ToTable && set.Contains(r.FromTable) && set.Contains(r.ToTable))
            .Distinct()
            .ToArray();
        var order = new Dictionary<TableDefinition, int>();
        var remaining = planned
            .OrderByDescending(table => depth[table])
            .ThenBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(table => Pending(table).Length == 0);
            if (next is null)
            {
                // Every remaining table waits on another remaining table: break the cycle where it costs the fewest
                // deferred nullable foreign keys. If every cycle has a nullable edge, some table has only nullable
                // pending edges; otherwise following non-nullable edges from any table must loop, and that loop is
                // the cycle no order can satisfy.
                var candidate = remaining
                    .Select(table => (Table: table, Edges: Pending(table)))
                    .Where(item => item.Edges.All(deferrable))
                    .OrderBy(item => item.Edges.Length)
                    .FirstOrDefault();
                if (candidate.Table is null)
                    throw new UnorderablePlanException(Describe(NonNullableCycle(remaining[0])));
                deferred.AddRange(candidate.Edges);
                next = candidate.Table;
            }
            order[next] = order.Count;
            remaining.Remove(next);
        }
        return new ImportOrder(order, levelled, deferred);

        ClosureRelationship[] Pending(TableDefinition table) =>
            cross.Where(r => r.FromTable == table && !order.ContainsKey(r.ToTable) && !deferred.Contains(r)).ToArray();

        IReadOnlyList<ClosureRelationship> NonNullableCycle(TableDefinition start)
        {
            var visited = new List<TableDefinition>();
            var walk = new List<ClosureRelationship>();
            var current = start;
            while (!visited.Contains(current))
            {
                visited.Add(current);
                var edge = Pending(current).First(r => !deferrable(r));
                walk.Add(edge);
                current = edge.ToTable;
            }
            return walk.Skip(visited.IndexOf(current)).ToArray();
        }
    }

    private static string Describe(IReadOnlyList<ClosureRelationship> cycle) =>
        "No write order satisfies the foreign keys between "
        + string.Join(", ", cycle.Select(r => Name(r.FromTable)).Distinct())
        + " with constraints enforced: "
        + string.Join(
            "; ",
            cycle.Select(r =>
                $"{r.Name} ({Name(r.FromTable)} -> {Name(r.ToTable)}, {Columns(r)} not nullable in the target)"
            )
        )
        + ". Every cycle needs at least one nullable foreign-key column in the target so DataPitcher can fill it in after the rows are written; constraints are never disabled.";

    private static string Name(TableDefinition table) => table.Schema + "." + table.Name;

    private static string Columns(ClosureRelationship relationship) => string.Join(", ", relationship.FromColumns);
}
