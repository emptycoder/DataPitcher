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
    [Fact] public void CondensedGraph_TopologicalLayers_RespectEveryEdge() { var (g, _, _, _) = G(("A", "B"), ("B", "C")); var condensed = new CondensedGraph(g); var layer = condensed.TopologicalLayers().SelectMany((x, i) => x.Select(id => (id, i))).ToDictionary(x => x.id, x => x.i); Assert.All(condensed.Edges, edge => Assert.True(layer[edge.Parent] < layer[edge.Child])); }
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
        + "|" + string.Join(",", graph.Edges.Select(e => $"{e.Parent}->{e.Child}"))
        + "|" + string.Join(";", graph.TopologicalLayers().Select(l => $"[{string.Join(",", l)}]"));

    [Fact]
    public void DependencyGraphAndCondensedGraph_WhenInputOrderDiffersOnADiscriminatingFixture_ProduceIdenticalCanonicalOutput()
    {
        TableDefinition T(string schema, string name) => new(schema, name, [], null, []);
        ForeignKeyDefinition F(string fkName, TableDefinition child, TableDefinition parent) =>
            new(fkName, child, parent, ["Id"], ["Id"], true, true);

        var alpha = T("dbo", "Alpha");
        var alphaLower = T("dbo", "alpha");
        var bravo = T("dbo", "Bravo");
        var orderHyphen = T("dbo", "Order-Item");
        var orderUnderscore = T("dbo", "Order_Item");
        var orebro = T("dbo", "Orebro");
        var orebroDiacritic = T("dbo", "Örebro");
        var zulu = T("dbo", "Zulu");
        var customer = T("sales", "Customer");
        var invoice = T("sales", "Invoice");
        var cycleA = T("sales", "CycleA");
        var cycleB = T("sales", "CycleB");
        var angelica = T("sales", "Angelica");
        var anglar = T("sales", "Änglar");

        var tables = new[] { alpha, alphaLower, bravo, orderHyphen, orderUnderscore, orebro, orebroDiacritic, zulu, customer, invoice, cycleA, cycleB, angelica, anglar };

        var fkInvoiceCustomer = F("FK_Invoice_Customer", invoice, customer);
        var fkInvoiceOrderItem = F("FK_Invoice_OrderItem", invoice, orderHyphen);
        var fkBravoCustomer = F("FK_Bravo_Customer", bravo, customer);
        var fkAlphaAlpha = F("FK_Alpha_alpha", alpha, alphaLower);
        var fkZuluOrebro = F("FK_Zulu_Orebro", zulu, orebroDiacritic);
        var fkOrderItemUnderscore = F("FK_OrderItem_OrderItemUnderscore", orderHyphen, orderUnderscore);
        var fkAngelicaAnglar = F("FK_Angelica_Anglar", angelica, anglar);
        var fkCycleAB = F("FK_CycleA_CycleB", cycleA, cycleB);
        var fkCycleBA = F("FK_CycleB_CycleA", cycleB, cycleA);

        var fks = new[] { fkInvoiceCustomer, fkInvoiceOrderItem, fkBravoCustomer, fkAlphaAlpha, fkZuluOrebro, fkOrderItemUnderscore, fkAngelicaAnglar, fkCycleAB, fkCycleBA };

        var forward = new DependencyGraph(tables, fks);
        var reversed = new DependencyGraph(tables.Reverse().ToArray(), fks.Reverse().ToArray());

        var forwardRender = RenderFull(forward);
        var reversedRender = RenderFull(reversed);

        const string expectedCanonicalOrdinalOrder =
            "dbo.Alpha,dbo.Bravo,dbo.Order-Item,dbo.Order_Item,dbo.Orebro,dbo.Zulu,dbo.alpha,dbo.Örebro,sales.Angelica,sales.Customer,sales.CycleA,sales.CycleB,sales.Invoice,sales.Änglar" +
            "|dbo.Alpha=>[FK_Alpha_alpha:dbo.alpha];dbo.Bravo=>[FK_Bravo_Customer:sales.Customer];dbo.Order-Item=>[FK_OrderItem_OrderItemUnderscore:dbo.Order_Item];dbo.Order_Item=>[];dbo.Orebro=>[];dbo.Zulu=>[FK_Zulu_Orebro:dbo.Örebro];dbo.alpha=>[];dbo.Örebro=>[];sales.Angelica=>[FK_Angelica_Anglar:sales.Änglar];sales.Customer=>[];sales.CycleA=>[FK_CycleA_CycleB:sales.CycleB];sales.CycleB=>[FK_CycleB_CycleA:sales.CycleA];sales.Invoice=>[FK_Invoice_OrderItem:dbo.Order-Item,FK_Invoice_Customer:sales.Customer];sales.Änglar=>[]" +
            "|dbo.Alpha<=[];dbo.Bravo<=[];dbo.Order-Item<=[FK_Invoice_OrderItem:sales.Invoice];dbo.Order_Item<=[FK_OrderItem_OrderItemUnderscore:dbo.Order-Item];dbo.Orebro<=[];dbo.Zulu<=[];dbo.alpha<=[FK_Alpha_alpha:dbo.Alpha];dbo.Örebro<=[FK_Zulu_Orebro:dbo.Zulu];sales.Angelica<=[];sales.Customer<=[FK_Bravo_Customer:dbo.Bravo,FK_Invoice_Customer:sales.Invoice];sales.CycleA<=[FK_CycleB_CycleA:sales.CycleB];sales.CycleB<=[FK_CycleA_CycleB:sales.CycleA];sales.Invoice<=[];sales.Änglar<=[FK_Angelica_Anglar:sales.Angelica]" +
            "|0:[dbo.Alpha];1:[dbo.Bravo];2:[dbo.Order-Item];3:[dbo.Order_Item];4:[dbo.Orebro];5:[dbo.Zulu];6:[dbo.alpha];7:[dbo.Örebro];8:[sales.Angelica];9:[sales.Customer];10:[sales.CycleA,sales.CycleB];11:[sales.Invoice];12:[sales.Änglar]" +
            "|2->11,3->2,6->0,7->5,9->1,9->11,12->8" +
            "|[3,4,6,7,9,10,12];[0,1,2,5,8];[11]";

        Assert.Equal(expectedCanonicalOrdinalOrder, forwardRender);
        Assert.Equal(forwardRender, reversedRender);
    }

    private static string RenderFull(DependencyGraph graph)
    {
        string Q(TableDefinition t) => $"{t.Schema}.{t.Name}";
        var condensed = new CondensedGraph(graph);
        var tablesPart = string.Join(",", graph.Tables.Select(Q));
        var depsPart = string.Join(";", graph.Tables.Select(t => $"{Q(t)}=>[{string.Join(",", graph.DependenciesOf(t).Select(fk => fk.Name + ":" + Q(fk.ParentTable)))}]"));
        var dependentsPart = string.Join(";", graph.Tables.Select(t => $"{Q(t)}<=[{string.Join(",", graph.DependentsOf(t).Select(fk => fk.Name + ":" + Q(fk.ChildTable)))}]"));
        var componentsPart = string.Join(";", condensed.Components.Select(c => $"{c.Id}:[{string.Join(",", c.Tables.Select(Q))}]"));
        var edgesPart = string.Join(",", condensed.Edges.Select(e => $"{e.Parent}->{e.Child}"));
        var layersPart = string.Join(";", condensed.TopologicalLayers().Select(l => $"[{string.Join(",", l)}]"));
        return string.Join("|", tablesPart, depsPart, dependentsPart, componentsPart, edgesPart, layersPart);
    }

    [Fact]
    public void CondensedEdge_EndpointsAreNamedParentAndChild()
    {
        var customers = new TableDefinition("dbo", "Customers", [], null, []);
        var orders = new TableDefinition("dbo", "Orders", [], null, []);
        var fk = new ForeignKeyDefinition("FK_Orders_Customers", orders, customers, ["CustomerId"], ["Id"], true, true);
        var condensed = new CondensedGraph(new DependencyGraph([customers, orders], [fk]));
        var edge = Assert.Single(condensed.Edges);
        var customersComponent = condensed.Components.Single(c => c.Tables.Contains(customers));
        var ordersComponent = condensed.Components.Single(c => c.Tables.Contains(orders));
        Assert.Equal(customersComponent.Id, edge.Parent);
        Assert.Equal(ordersComponent.Id, edge.Child);
    }
    private static (DependencyGraph Graph, TableDefinition A, TableDefinition B, TableDefinition C) G(params (string Child, string Parent)[] edges) { var names = edges.SelectMany(x => new[] { x.Child, x.Parent }).Distinct().ToArray(); var tables = names.ToDictionary(x => x, x => new TableDefinition("dbo", x, [], null, [])); return (new DependencyGraph(tables.Values, edges.Select((x, i) => new ForeignKeyDefinition($"FK{i}", tables[x.Child], tables[x.Parent], ["Id"], ["Id"], true, true))), tables.GetValueOrDefault("A")!, tables.GetValueOrDefault("B")!, tables.GetValueOrDefault("C")!); }
}
