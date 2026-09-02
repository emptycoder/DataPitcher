using DataPitcher.Core.Graph; using DataPitcher.Core.Schema; using Xunit;
namespace DataPitcher.UnitTests.Graph;
public sealed class TarjanSccTests
{
    [Fact] public void TarjanScc_WhenGraphIsAcyclic_EachNodeIsItsOwnComponent() { var (g, a, b, _) = G(("A", "B"), ("B", "C")); Assert.All(TarjanScc.Find(g), c => Assert.Single(c.Tables)); }
    [Fact] public void TarjanScc_WhenTwoTablesFormCycle_ProducesSingleComponentOfSizeTwo() { var (g, a, b, _) = G(("A", "B"), ("B", "A")); Assert.Contains(TarjanScc.Find(g), c => c.Tables.Count == 2 && c.Tables.Contains(a) && c.Tables.Contains(b)); }
    [Fact] public void TarjanScc_WhenNodeHasOnlySelfEdge_DoesNotCreateMultiNodeComponent() { var (g, _, _, _) = G(("A", "A")); Assert.Single(Assert.Single(TarjanScc.Find(g)).Tables); }
    [Fact]
    public void CondensedGraph_IsAlwaysAcyclic()
    {
        var (g, _, _, _) = G(("A", "B"), ("B", "A"), ("B", "C"));
        var condensed = new CondensedGraph(g);
        Assert.True(condensed.Components.Count < g.Tables.Count, "Precondition: the A/B cycle must have genuinely collapsed into one component.");
        Assert.True(condensed.IsAcyclic());
    }
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
        Assert.Throws<NotSupportedException>(() => ((IList<TableDefinition>)scc.Tables)[0] = new TableDefinition("dbo", "Z", [], null, []));
        Assert.Throws<NotSupportedException>(() => ((IList<Scc>)condensed.Components)[0] = new Scc(99, []));
        Assert.Throws<NotSupportedException>(() => ((IList<CondensedEdge>)condensed.Edges)[0] = new CondensedEdge(0, 0));
        Assert.Throws<NotSupportedException>(() => ((IList<IReadOnlyList<int>>)layers)[0] = Array.Empty<int>());
        Assert.Throws<NotSupportedException>(() => ((IList<int>)layers[0])[0] = 0);
    }
    [Fact]
    public void CondensedGraph_WhenTwoForeignKeysConnectSameComponentPair_ProducesSingleEdge()
    {
        var (g, _, _, _) = G(("A", "B"), ("A", "B"));
        Assert.Single(new CondensedGraph(g).Edges);
    }
    [Fact]
    public void TarjanScc_WhenTableDependsOnCyclicPair_AllTablesAppearInExactlyOneComponent()
    {
        var (g, _, _, _) = G(("A", "B"), ("B", "A"), ("C", "A"));
        var components = TarjanScc.Find(g);
        Assert.Equal(g.Tables.Count, components.Sum(c => c.Tables.Count));
        Assert.All(g.Tables, table => Assert.Equal(1, components.Count(c => c.Tables.Contains(table))));
    }
    [Fact]
    public void CondensedGraph_TopologicalLayers_PlaceParentsBeforeChildren()
    {
        var customers = new TableDefinition("dbo", "Customers", [], null, []);
        var orders = new TableDefinition("dbo", "Orders", [], null, []);
        var fk = new ForeignKeyDefinition("FK_Orders_Customers", orders, customers, ["CustomerId"], ["Id"], true, true);
        var condensed = new CondensedGraph(new DependencyGraph([customers, orders], [fk]));
        var layers = condensed.TopologicalLayers();
        int LayerOf(TableDefinition table)
        {
            var id = condensed.Components.Single(c => c.Tables.Contains(table)).Id;
            return layers.ToList().FindIndex(layer => layer.Contains(id));
        }
        Assert.True(LayerOf(customers) < LayerOf(orders), "Customers (parent) must be in a strictly earlier transfer layer than Orders (child).");
    }
    [Fact]
    public void CondensedGraph_WhenInputOrderDiffers_ProducesIdenticalOutput()
    {
        var a = new TableDefinition("dbo", "A", [], null, []);
        var b = new TableDefinition("dbo", "B", [], null, []);
        var c = new TableDefinition("dbo", "C", [], null, []);
        var d = new TableDefinition("dbo", "D", [], null, []);
        var fkAB = new ForeignKeyDefinition("FK_AB", a, b, ["Id"], ["Id"], true, true);
        var fkCD = new ForeignKeyDefinition("FK_CD", c, d, ["Id"], ["Id"], true, true);
        var forward = new CondensedGraph(new DependencyGraph([a, b, c, d], [fkAB, fkCD]));
        var reversed = new CondensedGraph(new DependencyGraph([d, c, b, a], [fkCD, fkAB]));
        Assert.Equal(Render(forward), Render(reversed));
    }
    private static string Render(CondensedGraph graph) =>
        string.Join(";", graph.Components.Select(c => $"{c.Id}:[{string.Join(",", c.Tables.Select(t => $"{t.Schema}.{t.Name}"))}]"))
        + "|" + string.Join(",", graph.Edges.Select(e => $"{e.From}->{e.To}"))
        + "|" + string.Join(";", graph.TopologicalLayers().Select(l => $"[{string.Join(",", l)}]"));
    private static (DependencyGraph Graph, TableDefinition A, TableDefinition B, TableDefinition C) G(params (string Child, string Parent)[] edges) { var names = edges.SelectMany(x => new[] { x.Child, x.Parent }).Distinct().ToArray(); var tables = names.ToDictionary(x => x, x => new TableDefinition("dbo", x, [], null, [])); return (new DependencyGraph(tables.Values, edges.Select((x, i) => new ForeignKeyDefinition($"FK{i}", tables[x.Child], tables[x.Parent], ["Id"], ["Id"], true, true))), tables.GetValueOrDefault("A")!, tables.GetValueOrDefault("B")!, tables.GetValueOrDefault("C")!); }
}
