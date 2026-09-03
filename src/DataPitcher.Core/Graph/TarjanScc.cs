using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Graph;

public sealed class Scc
{
    public Scc(int id, IEnumerable<TableDefinition> tables)
    {
        Id = id;
        Tables = Array.AsReadOnly(tables.ToArray());
    }

    public int Id { get; }
    public IReadOnlyList<TableDefinition> Tables { get; }
}

public static class TarjanScc
{
    public static IReadOnlyList<Scc> Find(DependencyGraph graph)
    {
        var index = 0;
        var stack = new Stack<TableDefinition>();
        var active = new HashSet<TableDefinition>();
        var indices = new Dictionary<TableDefinition, int>();
        var low = new Dictionary<TableDefinition, int>();
        var result = new List<Scc>();

        void Visit(TableDefinition table)
        {
            indices[table] = low[table] = index++;
            stack.Push(table);
            active.Add(table);

            foreach (var edge in graph.DependenciesOf(table))
            {
                var parent = edge.ParentTable;
                if (!indices.ContainsKey(parent))
                {
                    Visit(parent);
                    low[table] = Math.Min(low[table], low[parent]);
                }
                else if (active.Contains(parent))
                {
                    low[table] = Math.Min(low[table], indices[parent]);
                }
            }

            if (low[table] != indices[table])
                return;

            var members = new List<TableDefinition>();
            TableDefinition member;
            do
            {
                member = stack.Pop();
                active.Remove(member);
                members.Add(member);
            } while (!ReferenceEquals(member, table));

            result.Add(new Scc(result.Count, members.OrderBy(QualifiedName, StringComparer.Ordinal)));
        }

        foreach (var table in graph.Tables)
            if (!indices.ContainsKey(table))
                Visit(table);

        return Array.AsReadOnly(
            result
                .OrderBy(component => QualifiedName(component.Tables[0]), StringComparer.Ordinal)
                .Select((component, id) => new Scc(id, component.Tables))
                .ToArray()
        );
    }

    private static string QualifiedName(TableDefinition table) => $"{table.Schema}.{table.Name}";
}
