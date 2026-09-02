using DataPitcher.Core.Graph; using DataPitcher.Core.Schema; using System.Linq; using Xunit;
namespace DataPitcher.UnitTests.Graph;
public sealed class DependencyGraphTests
{
    [Fact] public void DependencyGraph_WhenOrderReferencesCustomer_EdgeRunsFromOrderToCustomer()
    { var orders = T("Orders"); var customers = T("Customers"); var fk = F("Customer", orders, customers); var graph = new DependencyGraph([orders, customers], [fk]); Assert.Equal(fk, Assert.Single(graph.DependenciesOf(orders))); Assert.Equal(fk, Assert.Single(graph.DependentsOf(customers))); }
    [Fact] public void DependencyGraph_WhenTwoForeignKeysBetweenSameTables_KeepsBothEdgesDistinct()
    { var orders = T("Orders"); var people = T("People"); var billTo = F("BillTo", orders, people); var shipTo = F("ShipTo", orders, people); var graph = new DependencyGraph([orders, people], [billTo, shipTo]); var edges = graph.DependenciesOf(orders); Assert.Equal(2, edges.Count); Assert.Contains(billTo, edges); Assert.Contains(shipTo, edges); }
    [Fact] public void DependencyGraph_WhenForeignKeyIsSelfReferencing_ProducesSelfEdge()
    { var employees = T("Employees"); var fk = F("Manager", employees, employees); var graph = new DependencyGraph([employees], [fk]); Assert.Equal(fk, Assert.Single(graph.DependenciesOf(employees))); Assert.Equal(fk, Assert.Single(graph.DependentsOf(employees))); }
    [Fact] public void DependencyGraph_WhenForeignKeyUsesRehydratedTableDefinitions_UsesTheMatchingGraphNodes()
    { var orders = T("Orders"); var customers = T("Customers"); var fk = F("Customer", T("Orders"), T("Customers")); var graph = new DependencyGraph([orders, customers], [fk]); Assert.Equal(fk, Assert.Single(graph.DependenciesOf(orders))); Assert.Equal(fk, Assert.Single(graph.DependentsOf(customers))); }
    [Fact] public void DependencyGraph_WhenForeignKeyEndpointsCarryPoorerMetadata_ExposesCanonicalNodeInstances()
    { var canonicalOrders = Rich("Orders", 3); var canonicalCustomers = Rich("Customers", 2); var fk = F("Customer", T("Orders"), T("Customers")); var graph = new DependencyGraph([canonicalOrders, canonicalCustomers], [fk]); var edge = Assert.Single(graph.DependenciesOf(canonicalOrders)); Assert.NotNull(edge.ParentTable.PrimaryKey); Assert.Equal(canonicalCustomers.Columns.Count, edge.ParentTable.Columns.Count); }
    [Fact] public void DependencyGraph_WhenTableAppearsTwice_IsRejected()
    { Assert.Throws<ArgumentException>(() => new DependencyGraph([T("Orders"), T("Orders")], [])); }
    [Fact] public void DependencyGraph_WhenInputsOrReturnedViewsAreMutated_RemainsImmutable()
    {
        var orders = T("Orders"); var customers = T("Customers"); var foreignKey = F("Customer", orders, customers); var tables = new List<TableDefinition> { orders, customers }; var foreignKeys = new List<ForeignKeyDefinition> { foreignKey };
        var graph = new DependencyGraph(tables, foreignKeys); tables.Clear(); foreignKeys.Clear();
        Assert.Equal(2, graph.Tables.Count); Assert.Single(graph.DependenciesOf(orders));
        Assert.Throws<NotSupportedException>(() => ((IList<TableDefinition>)graph.Tables).RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => ((IList<ForeignKeyDefinition>)graph.DependenciesOf(orders)).Add(foreignKey));
        Assert.Throws<NotSupportedException>(() => ((IList<ForeignKeyDefinition>)graph.DependentsOf(customers)).Add(foreignKey));
        Assert.Throws<NotSupportedException>(() => ((IList<TableDefinition>)graph.Tables)[0] = orders);
        Assert.Throws<NotSupportedException>(() => ((IList<ForeignKeyDefinition>)graph.DependenciesOf(orders))[0] = foreignKey);
        Assert.Throws<NotSupportedException>(() => ((IList<ForeignKeyDefinition>)graph.DependentsOf(customers))[0] = foreignKey);
    }
    private static TableDefinition T(string name) => new("dbo", name, [], null, []);
    private static TableDefinition Rich(string name, int columnCount) => new("dbo", name, Enumerable.Range(1, columnCount).Select(i => new ColumnDefinition($"Col{i}", typeof(int), false)).ToArray(), new UniqueConstraint($"PK_{name}", ["Col1"]), []);
    private static ForeignKeyDefinition F(string name, TableDefinition child, TableDefinition parent) => new(name, child, parent, ["Id"], ["Id"], true, true);
}
