namespace DataPitcher.Core.Graph;

public sealed record CondensedEdge(int From, int To);

public sealed class CondensedGraph
{
    public CondensedGraph(DependencyGraph graph)
    {
        Components = Array.AsReadOnly(TarjanScc.Find(graph).ToArray());
        var owner = Components
            .SelectMany(component => component.Tables.Select(table => (table, component.Id)))
            .ToDictionary(x => x.table, x => x.Id);
        Edges = Array.AsReadOnly(graph.Tables
            .SelectMany(graph.DependenciesOf)
            .Select(foreignKey => new CondensedEdge(owner[foreignKey.ChildTable], owner[foreignKey.ParentTable]))
            .Where(edge => edge.From != edge.To)
            .Distinct()
            .ToArray());
    }

    public IReadOnlyList<Scc> Components { get; }
    public IReadOnlyList<CondensedEdge> Edges { get; }

    public bool IsAcyclic()
    {
        var seen = 0;
        var pending = Components.ToDictionary(component => component.Id, _ => 0);
        foreach (var edge in Edges)
            pending[edge.To]++;

        var queue = new Queue<int>(pending.Where(x => x.Value == 0).Select(x => x.Key));
        while (queue.TryDequeue(out var id))
        {
            seen++;
            foreach (var edge in Edges.Where(x => x.From == id))
                if (--pending[edge.To] == 0)
                    queue.Enqueue(edge.To);
        }

        return seen == Components.Count;
    }

    public IReadOnlyList<IReadOnlyList<int>> TopologicalLayers()
    {
        var pending = Components.ToDictionary(component => component.Id, _ => 0);
        foreach (var edge in Edges)
            pending[edge.To]++;

        var layers = new List<IReadOnlyList<int>>();
        var next = pending.Where(x => x.Value == 0).Select(x => x.Key).ToArray();
        while (next.Length > 0)
        {
            layers.Add(Array.AsReadOnly(next));
            next = next
                .SelectMany(id => Edges.Where(edge => edge.From == id))
                .Where(edge => --pending[edge.To] == 0)
                .Select(edge => edge.To)
                .ToArray();
        }

        return Array.AsReadOnly(layers.ToArray());
    }
}
