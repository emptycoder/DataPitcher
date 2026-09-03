using DataPitcher.Core.Closure;
using DataPitcher.Core.Schema;

namespace DataPitcher.Application.Plans;

/// <summary>
/// The order tables are written in, the self references levelled through the sealed keys, the relationships whose
/// columns are filled in after every table has been written, and the tables no order can satisfy.
/// </summary>
public sealed record ImportOrder(
    IReadOnlyDictionary<TableDefinition, int> Order,
    IReadOnlyCollection<ClosureRelationship> Levelled,
    IReadOnlyCollection<ClosureRelationship> Deferred,
    IReadOnlySet<TableDefinition> Blocked
);

/// <summary>
/// Derives the import order from the table graph so that foreign keys stay enforced on the target: parents before
/// children (Kahn's algorithm), one self reference per table levelled through the sealed keys, and cycles broken at
/// nullable foreign keys whose values are written in a second pass. A cycle without a nullable edge is blocked and
/// falls back to deepest-generation-first.
/// </summary>
public static class ImportOrdering
{
    public static ImportOrder Plan(
        IReadOnlyList<TableDefinition> planned,
        IReadOnlyCollection<ClosureRelationship> relationships,
        IReadOnlyDictionary<TableDefinition, int> depth,
        Func<ClosureRelationship, bool> deferrable,
        Func<ClosureRelationship, bool> levelable
    )
    {
        var set = planned.ToHashSet();
        var levelled = new List<ClosureRelationship>();
        var deferred = new List<ClosureRelationship>();
        var blocked = new HashSet<TableDefinition>();
        foreach (var table in planned)
        {
            var self = relationships.Where(r => r.FromTable == table && r.ToTable == table).ToArray();
            if (self.Length == 0)
                continue;
            // One self reference onto the stable key is levelled; every other one is deferred or cannot be ordered.
            var level = self.FirstOrDefault(levelable);
            if (level is not null)
                levelled.Add(level);
            foreach (var relationship in self.Where(r => r != level))
                if (deferrable(relationship))
                    deferred.Add(relationship);
                else
                    blocked.Add(table);
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
                // deferred nullable foreign keys.
                var candidate = remaining
                    .Select(table => (Table: table, Edges: Pending(table)))
                    .Where(item => item.Edges.All(deferrable))
                    .OrderBy(item => item.Edges.Length)
                    .FirstOrDefault();
                if (candidate.Table is not null)
                {
                    deferred.AddRange(candidate.Edges);
                    next = candidate.Table;
                }
                else
                {
                    next = remaining[0];
                    blocked.Add(next);
                }
            }
            order[next] = order.Count;
            remaining.Remove(next);
        }
        return new ImportOrder(order, levelled, deferred, blocked);

        ClosureRelationship[] Pending(TableDefinition table) =>
            cross.Where(r => r.FromTable == table && !order.ContainsKey(r.ToTable) && !deferred.Contains(r)).ToArray();
    }
}
