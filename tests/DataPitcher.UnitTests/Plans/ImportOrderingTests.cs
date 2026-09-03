using DataPitcher.Application.Plans;
using DataPitcher.Core.Closure;
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Plans;

public sealed class ImportOrderingTests
{
    [Fact]
    public void Plan_WritesParentsBeforeChildren()
    {
        var parent = Table("Parent");
        var child = Table("Child", new ColumnDefinition("ParentId", typeof(int), false));
        var order = ImportOrdering.Plan(
            [child, parent],
            [Reference(child, "ParentId", parent)],
            Depth((child, 0), (parent, 1)),
            Nullable,
            OntoKey
        );

        Assert.True(order.Order[parent] < order.Order[child]);
        Assert.Empty(order.Deferred);
        Assert.Empty(order.Blocked);
    }

    [Fact]
    public void Plan_BreaksACycleAtTheNullableForeignKeyAndDefersIt()
    {
        var teams = Table("Teams", new ColumnDefinition("LeadId", typeof(int), true));
        var people = Table("People", new ColumnDefinition("TeamId", typeof(int), false));
        var lead = Reference(teams, "LeadId", people);
        var order = ImportOrdering.Plan(
            [people, teams],
            [Reference(people, "TeamId", teams), lead],
            Depth((people, 0), (teams, 1)),
            Nullable,
            OntoKey
        );

        Assert.True(order.Order[teams] < order.Order[people]);
        Assert.Equal([lead], order.Deferred);
        Assert.Empty(order.Blocked);
    }

    [Fact]
    public void Plan_MarksACycleWithoutANullableEdgeAsBlocked()
    {
        var a = Table("A", new ColumnDefinition("BId", typeof(int), false));
        var b = Table("B", new ColumnDefinition("AId", typeof(int), false));
        var order = ImportOrdering.Plan(
            [a, b],
            [Reference(a, "BId", b), Reference(b, "AId", a)],
            Depth((a, 0), (b, 1)),
            Nullable,
            OntoKey
        );

        Assert.Equal(2, order.Order.Count);
        Assert.Empty(order.Deferred);
        Assert.Single(order.Blocked);
    }

    [Fact]
    public void Plan_LevelsOneSelfReferenceAndDefersTheOthers()
    {
        var nodes = Table(
            "Nodes",
            new ColumnDefinition("ParentId", typeof(int), true),
            new ColumnDefinition("RootId", typeof(int), true)
        );
        var parent = Reference(nodes, "ParentId", nodes);
        var root = Reference(nodes, "RootId", nodes);
        var order = ImportOrdering.Plan([nodes], [parent, root], Depth((nodes, 0)), Nullable, OntoKey);

        Assert.Equal([parent], order.Levelled);
        Assert.Equal([root], order.Deferred);
        Assert.Empty(order.Blocked);
    }

    private static TableDefinition Table(string name, params ColumnDefinition[] columns) =>
        new(
            "dbo",
            name,
            [new ColumnDefinition("Id", typeof(int), false), .. columns],
            new UniqueConstraint("PK_" + name, ["Id"]),
            []
        );

    private static ClosureRelationship Reference(TableDefinition child, string column, TableDefinition parent) =>
        new(new ForeignKeyDefinition("FK_" + child.Name + "_" + column, child, parent, [column], ["Id"], true, true));

    private static Dictionary<TableDefinition, int> Depth(params (TableDefinition Table, int Depth)[] depths) =>
        depths.ToDictionary(item => item.Table, item => item.Depth);

    private static bool Nullable(ClosureRelationship relationship) =>
        relationship.FromColumns.All(name =>
            relationship.FromTable.Columns.Single(column => column.Name == name).IsNullable
        );

    private static bool OntoKey(ClosureRelationship relationship) =>
        relationship.ToColumns.SequenceEqual(["Id"], StringComparer.Ordinal);
}
