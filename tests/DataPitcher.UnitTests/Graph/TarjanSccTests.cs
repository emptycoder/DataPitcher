using DataPitcher.Core.Graph; using DataPitcher.Core.Schema; using Xunit;
namespace DataPitcher.UnitTests.Graph;
public sealed class TarjanSccTests
{
    [Fact] public void TarjanScc_WhenGraphIsAcyclic_EachNodeIsItsOwnComponent() { var (g, a, b, _) = G(("A", "B"), ("B", "C")); Assert.All(TarjanScc.Find(g), c => Assert.Single(c.Tables)); }
    [Fact] public void TarjanScc_WhenTwoTablesFormCycle_ProducesSingleComponentOfSizeTwo() { var (g, a, b, _) = G(("A", "B"), ("B", "A")); Assert.Contains(TarjanScc.Find(g), c => c.Tables.Count == 2 && c.Tables.Contains(a) && c.Tables.Contains(b)); }
    [Fact] public void TarjanScc_WhenNodeHasOnlySelfEdge_DoesNotCreateMultiNodeComponent() { var (g, _, _, _) = G(("A", "A")); Assert.Single(Assert.Single(TarjanScc.Find(g)).Tables); }
    [Fact] public void CondensedGraph_IsAlwaysAcyclic() { var (g, _, _, _) = G(("A", "B"), ("B", "A"), ("B", "C")); Assert.True(new CondensedGraph(g).IsAcyclic()); }
    [Fact] public void CondensedGraph_TopologicalLayers_RespectEveryEdge() { var (g, _, _, _) = G(("A", "B"), ("B", "C")); var condensed = new CondensedGraph(g); var layer = condensed.TopologicalLayers().SelectMany((x, i) => x.Select(id => (id, i))).ToDictionary(x => x.id, x => x.i); Assert.All(condensed.Edges, edge => Assert.True(layer[edge.From] < layer[edge.To])); }
    [Fact] public void SccAndCondensedGraph_WhenInputOrReturnedViewsAreMutated_RemainImmutable()
    {
        var tables = new List<TableDefinition> { new("dbo", "A", [], null, []) }; var scc = new Scc(1, tables); tables.Clear();
        Assert.Single(scc.Tables); Assert.Throws<NotSupportedException>(() => ((IList<TableDefinition>)scc.Tables).RemoveAt(0));
        var (graph, _, _, _) = G(("A", "B")); var condensed = new CondensedGraph(graph); var layers = condensed.TopologicalLayers();
        Assert.Throws<NotSupportedException>(() => ((IList<Scc>)condensed.Components).RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => ((IList<CondensedEdge>)condensed.Edges).RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => ((IList<IReadOnlyList<int>>)layers).RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => ((IList<int>)layers[0]).RemoveAt(0));
    }
    private static (DependencyGraph Graph, TableDefinition A, TableDefinition B, TableDefinition C) G(params (string Child, string Parent)[] edges) { var names = edges.SelectMany(x => new[] { x.Child, x.Parent }).Distinct().ToArray(); var tables = names.ToDictionary(x => x, x => new TableDefinition("dbo", x, [], null, [])); return (new DependencyGraph(tables.Values, edges.Select((x, i) => new ForeignKeyDefinition($"FK{i}", tables[x.Child], tables[x.Parent], ["Id"], ["Id"], true, true))), tables.GetValueOrDefault("A")!, tables.GetValueOrDefault("B")!, tables.GetValueOrDefault("C")!); }
}
