using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using Xunit;

namespace DataPitcher.UnitTests.Selection;

public sealed class SelectionQueryValidationTests
{
    [Fact]
    public void SelectionQuery_AcceptsEveryTypedOperatorAndNestedBooleanTree()
    {
        var orders = T("sales", "Orders", ("Id", typeof(int), false), ("Name", typeof(string), true), ("Active", typeof(bool), false), ("Day", typeof(DateOnly), false), ("At", typeof(TimeOnly), false), ("Occurred", typeof(DateTime), false));
        var lines = T("sales", "Lines", ("Id", typeof(int), false), ("OrderId", typeof(int), false));
        var fk = new ForeignKeyDefinition("FK_Lines_Orders", lines, orders, ["OrderId"], ["Id"], true, true);
        var root = new SelectionTableReference(orders, "o");
        var p = SelectionQueryTestData.OperatorQueries().Select(query => query.Predicate!).ToArray();
        var query = new SelectionQuery(new([orders, lines], [fk]), root, Key(orders), [new ForeignKeyJoin("o", "l0", fk, RelationshipDirection.Reverse)], new NotPredicate(new OrPredicate([new AndPredicate(p), new NotPredicate(p[0])]))) ;
        Assert.Equal(orders, query.Root.Table); Assert.Single(query.Joins);
    }

    [Theory]
    [InlineData("9bad", "Alias must match")]
    [InlineData("o", "Alias is already in use")]
    public void SelectionQuery_RejectsInvalidManualJoinAlias(string alias, string message)
    {
        var orders = T("sales", "Orders", ("Id", typeof(int), false)); var other = T("sales", "Other", ("OrderId", typeof(int), false));
        var error = Assert.Throws<ArgumentException>(() => new SelectionQuery(new([orders, other], []), new(orders, "o"), Key(orders), [new ManualJoin("o", alias, other, [new("Id", "OrderId")])], null));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void SelectionQuery_RejectsUnknownManualColumnsIncompatibleTypesAndIncorrectParameterTypes()
    {
        var orders = T("sales", "Orders", ("Id", typeof(int), false)); var other = T("sales", "Other", ("Code", typeof(string), false)); var schema = new SelectionSchema([orders, other], []);
        Assert.Contains("does not exist", Assert.Throws<ArgumentException>(() => new SelectionQuery(schema, new(orders, "o"), Key(orders), [new ManualJoin("o", "x", other, [new("Missing", "Code")])], null)).Message);
        Assert.Contains("identical CLR types", Assert.Throws<ArgumentException>(() => new SelectionQuery(schema, new(orders, "o"), Key(orders), [new ManualJoin("o", "x", other, [new("Id", "Code")])], null)).Message);
        Assert.Contains("parameter CLR type", Assert.Throws<ArgumentException>(() => new SelectionQuery(schema, new(orders, "o"), Key(orders), [], new ComparisonPredicate(new("o", "Id"), SelectionComparison.Equal, V<string>("1")))).Message);
    }

    private static SelectionParameterValue V<T>(T value) where T : notnull => new(typeof(T), value);
    private static StableKeySelection Key(TableDefinition table) => new(new UniqueConstraint("PK_" + table.Name, ["Id"]));
    private static TableDefinition T(string schema, string name, params (string Name, Type Type, bool Nullable)[] columns) => new(schema, name, columns.Select(c => new ColumnDefinition(c.Name, c.Type, c.Nullable)).ToArray(), new UniqueConstraint("PK_" + name, ["Id"]), []);
}

internal static class SelectionQueryTestData
{
    private static readonly TableDefinition Orders = new("sales", "Orders", [new("Id", typeof(int), false), new("Name", typeof(string), true), new("Active", typeof(bool), false), new("Day", typeof(DateOnly), false), new("At", typeof(TimeOnly), false), new("Occurred", typeof(DateTime), false)], new("PK_Orders", ["Id"]), []);
    public static SelectionQuery Query(SelectionPredicate predicate) => new(new([Orders], []), new(Orders, "o"), new(Orders.PrimaryKey), [], predicate);
    public static ComparisonPredicate Id(int value) => new(new("o", "Id"), SelectionComparison.Equal, new(typeof(int), value));
    public static SelectionQuery QuotedRootAndJoin()
    {
        var orders = new TableDefinition("sales", "Order\"Rows", [new("Id", typeof(int), false), new("CustomerId", typeof(int), false)], new("PK_OrderRows", ["Id"]), []);
        var customers = new TableDefinition("sales", "Customers", [new("Id", typeof(int), false), new("RegionId", typeof(int), false)], new("PK_Customers", ["Id"]), []);
        var regions = new TableDefinition("sales", "Regions", [new("Id", typeof(int), false)], new("PK_Regions", ["Id"]), []);
        var foreignKey = new ForeignKeyDefinition("FK_Order_Customer", orders, customers, ["CustomerId"], ["Id"], true, true);
        return new(new([orders, customers, regions], [foreignKey]), new(orders, "r"), new(orders.PrimaryKey), [new ForeignKeyJoin("r", "c", foreignKey, RelationshipDirection.Forward), new ManualJoin("c", "g", regions, [new("RegionId", "Id")])], null);
    }
    public static IEnumerable<SelectionQuery> OperatorQueries()
    {
        SelectionParameterValue V<T>(T value) where T : notnull => new(typeof(T), value); var column = new SelectionColumn("o", "Id");
        foreach (var predicate in new SelectionPredicate[] { new ComparisonPredicate(column, SelectionComparison.Equal, V(1)), new ComparisonPredicate(column, SelectionComparison.NotEqual, V(2)), new ComparisonPredicate(column, SelectionComparison.GreaterThan, V(3)), new ComparisonPredicate(column, SelectionComparison.GreaterOrEqual, V(4)), new ComparisonPredicate(column, SelectionComparison.LessThan, V(5)), new ComparisonPredicate(column, SelectionComparison.LessOrEqual, V(6)), new BetweenPredicate(column, V(7), V(8)), new SetPredicate(column, false, [V(9)]), new SetPredicate(column, true, [V(10)]), new NullPredicate(new("o", "Name"), false), new NullPredicate(new("o", "Name"), true), new TextPredicate(new("o", "Name"), TextMatch.Contains, V("a")), new TextPredicate(new("o", "Name"), TextMatch.StartsWith, V("b")), new TextPredicate(new("o", "Name"), TextMatch.EndsWith, V("c")), new BooleanPredicate(new("o", "Active"), V(true)), new TemporalRangePredicate(new("o", "Day"), TemporalKind.Date, V(new DateOnly(2026, 9, 2)), V(new DateOnly(2026, 9, 3))), new TemporalRangePredicate(new("o", "At"), TemporalKind.Time, V(new TimeOnly(9, 0)), V(new TimeOnly(10, 0))), new TemporalRangePredicate(new("o", "Occurred"), TemporalKind.DateTime, V(new DateTime(2026, 9, 2)), V(new DateTime(2026, 9, 3))) }) yield return Query(predicate);
        var lines = new TableDefinition("sales", "Lines", [new("Id", typeof(int), false), new("OrderId", typeof(int), false)], new("PK_Lines", ["Id"]), []);
        yield return new(new([Orders, lines], []), new(Orders, "o"), new(Orders.PrimaryKey), [], new ExistsPredicate(lines, "l", [new(new("o", "Id"), "OrderId")], null, false));
        yield return new(new([Orders, lines], []), new(Orders, "o"), new(Orders.PrimaryKey), [], new ExistsPredicate(lines, "l", [new(new("o", "Id"), "OrderId")], null, true));
    }
}
