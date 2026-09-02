using DataPitcher.Core.Graph; using DataPitcher.Core.Schema; using Xunit;
namespace DataPitcher.UnitTests.Graph;
public sealed class DependencyGraphTests
{
    [Fact] public void DependencyGraph_WhenOrderReferencesCustomer_EdgeRunsFromOrderToCustomer()
    { var orders = T("Orders"); var customers = T("Customers"); var fk = F("Customer", orders, customers); var graph = new DependencyGraph([orders, customers], [fk]); Assert.Equal(fk, Assert.Single(graph.DependenciesOf(orders))); Assert.Equal(fk, Assert.Single(graph.DependentsOf(customers))); }
    [Fact] public void DependencyGraph_WhenTwoForeignKeysBetweenSameTables_KeepsBothEdgesDistinct()
    { var orders = T("Orders"); var people = T("People"); var graph = new DependencyGraph([orders, people], [F("BillTo", orders, people), F("ShipTo", orders, people)]); Assert.Equal(2, graph.DependenciesOf(orders).Count); }
    [Fact] public void DependencyGraph_WhenForeignKeyIsSelfReferencing_ProducesSelfEdge()
    { var employees = T("Employees"); var fk = F("Manager", employees, employees); var graph = new DependencyGraph([employees], [fk]); Assert.Equal(fk, Assert.Single(graph.DependenciesOf(employees))); }
    [Fact] public void DependencyGraph_WhenForeignKeyUsesRehydratedTableDefinitions_UsesTheMatchingGraphNodes()
    { var orders = T("Orders"); var customers = T("Customers"); var fk = F("Customer", T("Orders"), T("Customers")); var graph = new DependencyGraph([orders, customers], [fk]); Assert.Equal(fk, Assert.Single(graph.DependenciesOf(orders))); Assert.Equal(fk, Assert.Single(graph.DependentsOf(customers))); }
    [Fact] public void DependencyGraph_WhenInputsOrReturnedViewsAreMutated_RemainsImmutable()
    {
        var orders = T("Orders"); var customers = T("Customers"); var foreignKey = F("Customer", orders, customers); var tables = new List<TableDefinition> { orders, customers }; var foreignKeys = new List<ForeignKeyDefinition> { foreignKey };
        var graph = new DependencyGraph(tables, foreignKeys); tables.Clear(); foreignKeys.Clear();
        Assert.Equal(2, graph.Tables.Count); Assert.Single(graph.DependenciesOf(orders));
        Assert.Throws<NotSupportedException>(() => ((IList<TableDefinition>)graph.Tables).RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => ((IList<ForeignKeyDefinition>)graph.DependenciesOf(orders)).Add(foreignKey));
        Assert.Throws<NotSupportedException>(() => ((IList<ForeignKeyDefinition>)graph.DependentsOf(customers)).Add(foreignKey));
    }
    private static TableDefinition T(string name) => new("dbo", name, [], null, []);
    private static ForeignKeyDefinition F(string name, TableDefinition child, TableDefinition parent) => new(name, child, parent, ["Id"], ["Id"], true, true);
}
