namespace DataPitcher.Core.Graph;

/// <summary>Edges run from parent to child in transfer order, deliberately the inverse of dependency graph child-to-parent edges.</summary>
public sealed record CondensedEdge(int Parent, int Child);

public sealed class CondensedGraph
{
    public CondensedGraph(DependencyGraph graph)
    {
        Components = Array.AsReadOnly(TarjanScc.Find(graph).ToArray());
        var owner = Components
            .SelectMany(component => component.Tables.Select(table => (table, component.Id)))
            .ToDictionary(x => x.table, x => x.Id);
        Edges = Array.AsReadOnly(
            graph
                .Tables.SelectMany(graph.DependenciesOf)
                .Select(foreignKey => new CondensedEdge(owner[foreignKey.ParentTable], owner[foreignKey.ChildTable]))
                .Where(edge => edge.Parent != edge.Child)
                .Distinct()
                .OrderBy(edge => edge.Parent)
                .ThenBy(edge => edge.Child)
                .ToArray()
        );
    }

    public IReadOnlyList<Scc> Components { get; }
    public IReadOnlyList<CondensedEdge> Edges { get; }

    public bool IsAcyclic()
    {
        var seen = 0;
        var pending = Components.ToDictionary(component => component.Id, _ => 0);
        var outgoing = Edges.GroupBy(edge => edge.Parent).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var edge in Edges)
            pending[edge.Child]++;

        var queue = new Queue<int>(pending.Where(x => x.Value == 0).Select(x => x.Key).OrderBy(id => id));
        while (queue.TryDequeue(out var id))
        {
            seen++;
            if (outgoing.TryGetValue(id, out var edges))
                foreach (var edge in edges)
                    if (--pending[edge.Child] == 0)
                        queue.Enqueue(edge.Child);
        }

        return seen == Components.Count;
    }

    public IReadOnlyList<IReadOnlyList<int>> TopologicalLayers()
    {
        var pending = Components.ToDictionary(component => component.Id, _ => 0);
        var outgoing = Edges.GroupBy(edge => edge.Parent).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var edge in Edges)
            pending[edge.Child]++;

        var layers = new List<IReadOnlyList<int>>();
        var next = pending.Where(x => x.Value == 0).Select(x => x.Key).OrderBy(id => id).ToArray();
        while (next.Length > 0)
        {
            layers.Add(Array.AsReadOnly(next));
            next = next.SelectMany(id => outgoing.TryGetValue(id, out var edges) ? edges : Array.Empty<CondensedEdge>())
                .Where(edge => --pending[edge.Child] == 0)
                .Select(edge => edge.Child)
                .OrderBy(id => id)
                .ToArray();
        }

        return Array.AsReadOnly(layers.ToArray());
    }
}
