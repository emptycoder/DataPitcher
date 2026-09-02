using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Providers.SqlServer;
using Xunit;

namespace DataPitcher.UnitTests.Selection;

public sealed class SqlServerSelectionSqlGeneratorTests
{
    [Fact]
    public void Compile_ProjectsOnlyAliasedDistinctRootKeys_WhenTheQueryJoinsOrderLines()
    {
        var sql = new SqlServerSelectionSqlGenerator().Compile(OrdersJoinedToLines());

        Assert.StartsWith("SELECT DISTINCT [o].[order_id] AS [__datapitcher_key_0]", sql.CommandText, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN [dbo].[order_lines] AS [l]", sql.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("[l].[line_id] AS [__datapitcher_key_", sql.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ImplementsTheCoreCompilerContract()
    {
        ISelectionSqlCompiler compiler = new SqlServerSelectionSqlGenerator();

        Assert.IsType<SqlServerSelectionSqlGenerator>(compiler);
    }

    [Fact]
    public void Compile_ProjectsCompositeKeysInDeclarationOrder()
    {
        var orders = new TableDefinition("dbo", "orders", [new("tenant_id", typeof(int), false), new("order_id", typeof(int), false)], new("PK_orders", ["tenant_id", "order_id"]), []);
        var sql = new SqlServerSelectionSqlGenerator().Compile(new(new([orders], []), new(orders, "o"), new(orders.PrimaryKey), [], null));

        Assert.StartsWith("SELECT DISTINCT [o].[tenant_id] AS [__datapitcher_key_0], [o].[order_id] AS [__datapitcher_key_1]", sql.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_QuotesIdentifiersAndKeepsSqlMetacharactersInParameters()
    {
        const string attack = "x%' OR 1=1; DROP TABLE orders; --";
        var sql = new SqlServerSelectionSqlGenerator().Compile(PredicateQuery(new TextPredicate(new("o", "name"), TextMatch.Contains, new(typeof(string), attack))));

        Assert.DoesNotContain(attack, sql.CommandText, StringComparison.Ordinal);
        Assert.Equal("@p0", sql.Parameters[0].Name);
        Assert.Equal(typeof(string), sql.Parameters[0].ClrType);
        Assert.Equal("x\\%' OR 1=1; DROP TABLE orders; --", sql.Parameters[0].Value);
        Assert.Contains("LIKE ('%' + @p0 + '%') ESCAPE '\\'", sql.CommandText, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PredicateCases))]
    public void Compile_RendersEveryValidatedPredicateAsParameterizedSql(SelectionPredicate predicate, string expectedSql, object[] expectedValues)
    {
        var sql = new SqlServerSelectionSqlGenerator().Compile(PredicateQuery(predicate));

        Assert.Contains(expectedSql, sql.CommandText, StringComparison.Ordinal);
        Assert.Equal(expectedValues, sql.Parameters.Select(parameter => parameter.Value));
        Assert.All(sql.Parameters, parameter => Assert.Contains(parameter.Name, sql.CommandText, StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_PairsCompositeForwardForeignKeyColumns()
    {
        var parent = new TableDefinition("dbo", "parents", [new("tenant_id", typeof(int), false), new("id", typeof(int), false)], new("PK_parents", ["tenant_id", "id"]), []);
        var child = new TableDefinition("dbo", "children", [new("id", typeof(int), false), new("parent_tenant_id", typeof(int), false), new("parent_id", typeof(int), false)], new("PK_children", ["id"]), []);
        var foreignKey = new ForeignKeyDefinition("FK_children_parents", child, parent, ["parent_tenant_id", "parent_id"], ["tenant_id", "id"], true, true);
        var query = new SelectionQuery(new([child, parent], [foreignKey]), new(child, "c"), new(child.PrimaryKey), [new ForeignKeyJoin("c", "p", foreignKey, RelationshipDirection.Forward)], null);

        Assert.Contains("[c].[parent_tenant_id] = [p].[tenant_id] AND [c].[parent_id] = [p].[id]", new SqlServerSelectionSqlGenerator().Compile(query).CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_PairsCompositeReverseForeignKeyColumns()
    {
        var parent = new TableDefinition("dbo", "parents", [new("tenant_id", typeof(int), false), new("id", typeof(int), false)], new("PK_parents", ["tenant_id", "id"]), []);
        var child = new TableDefinition("dbo", "children", [new("id", typeof(int), false), new("parent_tenant_id", typeof(int), false), new("parent_id", typeof(int), false)], new("PK_children", ["id"]), []);
        var foreignKey = new ForeignKeyDefinition("FK_children_parents", child, parent, ["parent_tenant_id", "parent_id"], ["tenant_id", "id"], true, true);
        var query = new SelectionQuery(new([child, parent], [foreignKey]), new(parent, "p"), new(parent.PrimaryKey), [new ForeignKeyJoin("p", "c", foreignKey, RelationshipDirection.Reverse)], null);

        Assert.Contains("[p].[tenant_id] = [c].[parent_tenant_id] AND [p].[id] = [c].[parent_id]", new SqlServerSelectionSqlGenerator().Compile(query).CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_RendersAnUnfilteredExistsPredicate()
    {
        var sql = new SqlServerSelectionSqlGenerator().Compile(PredicateQuery(new ExistsPredicate(Lines, "l", [new(new("o", "id"), "order_id")], null, false)));

        Assert.Contains("EXISTS (SELECT 1 FROM [dbo].[order_lines] AS [l] WHERE [o].[id] = [l].[order_id])", sql.CommandText, StringComparison.Ordinal);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Compile_RendersEveryExistsCorrelation()
    {
        var orders = new TableDefinition("dbo", "orders", [new("tenant_id", typeof(int), false), new("id", typeof(int), false)], new("PK_orders", ["tenant_id", "id"]), []);
        var lines = new TableDefinition("dbo", "order_lines", [new("order_tenant_id", typeof(int), false), new("order_id", typeof(int), false)], new("PK_order_lines", ["order_tenant_id", "order_id"]), []);
        var query = new SelectionQuery(new([orders, lines], []), new(orders, "o"), new(orders.PrimaryKey), [], new ExistsPredicate(lines, "l", [new(new("o", "tenant_id"), "order_tenant_id"), new(new("o", "id"), "order_id")], null, false));

        var sql = new SqlServerSelectionSqlGenerator().Compile(query);

        Assert.Contains("[o].[tenant_id] = [l].[order_tenant_id] AND [o].[id] = [l].[order_id]", sql.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnDefinition_DefaultsGeneratedMetadataAndPreservesItWhenSpecified()
    {
        Assert.False(new ColumnDefinition("id", typeof(int), false).IsGenerated);
        Assert.True(new ColumnDefinition("calculated", typeof(int), false, true).IsGenerated);
    }

    [Fact]
    public void Compile_IsInvariantUnderNonInvariantCultures()
    {
        var query = PredicateQuery(new TemporalRangePredicate(new("o", "day"), TemporalKind.Date, new(typeof(DateOnly), new DateOnly(2026, 9, 2)), new(typeof(DateOnly), new DateOnly(2026, 9, 3))));
        var generator = new SqlServerSelectionSqlGenerator();
        var invariant = generator.Compile(query).CommandText;
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(invariant, generator.Compile(query).CommandText);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }

    public static IEnumerable<object?[]> PredicateCases()
    {
        yield return [new AndPredicate([Comparison(SelectionComparison.Equal, 1), Comparison(SelectionComparison.GreaterThan, 2)]), "([o].[id] = @p0 AND [o].[id] > @p1)", new object[] { 1, 2 }];
        yield return [new OrPredicate([Comparison(SelectionComparison.Equal, 1), Comparison(SelectionComparison.GreaterThan, 2)]), "([o].[id] = @p0 OR [o].[id] > @p1)", new object[] { 1, 2 }];
        yield return [new NotPredicate(Comparison(SelectionComparison.NotEqual, 1)), "NOT ([o].[id] <> @p0)", new object[] { 1 }];
        yield return [Comparison(SelectionComparison.Equal, 1), "[o].[id] = @p0", new object[] { 1 }];
        yield return [Comparison(SelectionComparison.NotEqual, 1), "[o].[id] <> @p0", new object[] { 1 }];
        yield return [Comparison(SelectionComparison.GreaterThan, 1), "[o].[id] > @p0", new object[] { 1 }];
        yield return [Comparison(SelectionComparison.GreaterOrEqual, 1), "[o].[id] >= @p0", new object[] { 1 }];
        yield return [Comparison(SelectionComparison.LessThan, 1), "[o].[id] < @p0", new object[] { 1 }];
        yield return [Comparison(SelectionComparison.LessOrEqual, 1), "[o].[id] <= @p0", new object[] { 1 }];
        yield return [new BetweenPredicate(new("o", "id"), Value(1), Value(2)), "[o].[id] BETWEEN @p0 AND @p1", new object[] { 1, 2 }];
        yield return [new SetPredicate(new("o", "id"), false, [Value(2), Value(1)]), "[o].[id] IN (@p0, @p1)", new object[] { 1, 2 }];
        yield return [new SetPredicate(new("o", "id"), true, [Value(2), Value(1)]), "[o].[id] NOT IN (@p0, @p1)", new object[] { 1, 2 }];
        yield return [new NullPredicate(new("o", "name"), false), "[o].[name] IS NULL", Array.Empty<object>()];
        yield return [new NullPredicate(new("o", "name"), true), "[o].[name] IS NOT NULL", Array.Empty<object>()];
        yield return [new TextPredicate(new("o", "name"), TextMatch.Contains, new(typeof(string), "a%_\\")), "LIKE ('%' + @p0 + '%') ESCAPE '\\'", new object[] { "a\\%\\_\\\\" }];
        yield return [new TextPredicate(new("o", "name"), TextMatch.StartsWith, new(typeof(string), "value")), "LIKE (@p0 + '%') ESCAPE '\\'", new object[] { "value" }];
        yield return [new TextPredicate(new("o", "name"), TextMatch.EndsWith, new(typeof(string), "value")), "LIKE ('%' + @p0) ESCAPE '\\'", new object[] { "value" }];
        yield return [new BooleanPredicate(new("o", "active"), new(typeof(bool), true)), "[o].[active] = @p0", new object[] { true }];
        yield return [new TemporalRangePredicate(new("o", "day"), TemporalKind.Date, new(typeof(DateOnly), new DateOnly(2026, 9, 2)), new(typeof(DateOnly), new DateOnly(2026, 9, 3))), "[o].[day] BETWEEN @p0 AND @p1", new object[] { new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 3) }];
        yield return [new TemporalRangePredicate(new("o", "at"), TemporalKind.Time, new(typeof(TimeOnly), new TimeOnly(9, 0)), new(typeof(TimeOnly), new TimeOnly(10, 0))), "[o].[at] BETWEEN @p0 AND @p1", new object[] { new TimeOnly(9, 0), new TimeOnly(10, 0) }];
        yield return [new TemporalRangePredicate(new("o", "occurred"), TemporalKind.DateTime, new(typeof(DateTime), new DateTime(2026, 9, 2)), new(typeof(DateTime), new DateTime(2026, 9, 3))), "[o].[occurred] BETWEEN @p0 AND @p1", new object[] { new DateTime(2026, 9, 2), new DateTime(2026, 9, 3) }];
        yield return [new ExistsPredicate(Lines, "l", [new(new("o", "id"), "order_id")], Comparison(SelectionComparison.Equal, 7, "l"), true), "NOT EXISTS (SELECT 1 FROM [dbo].[order_lines] AS [l] WHERE [o].[id] = [l].[order_id] AND [l].[id] = @p0)", new object[] { 7 }];
    }

    private static readonly TableDefinition PredicateOrders = new("dbo", "orders", [new("id", typeof(int), false), new("name", typeof(string), true), new("active", typeof(bool), false), new("day", typeof(DateOnly), false), new("at", typeof(TimeOnly), false), new("occurred", typeof(DateTime), false)], new("PK_orders", ["id"]), []);
    private static readonly TableDefinition Lines = new("dbo", "order_lines", [new("id", typeof(int), false), new("order_id", typeof(int), false)], new("PK_order_lines", ["id"]), []);
    private static SelectionQuery OrdersJoinedToLines()
    {
        var orders = new TableDefinition("dbo", "orders", [new("order_id", typeof(int), false)], new("PK_orders", ["order_id"]), []);
        var lines = new TableDefinition("dbo", "order_lines", [new("line_id", typeof(int), false), new("order_id", typeof(int), false)], new("PK_order_lines", ["line_id"]), []);
        return new(new([orders, lines], []), new(orders, "o"), new(orders.PrimaryKey), [new ManualJoin("o", "l", lines, [new("order_id", "order_id")])], null);
    }
    private static SelectionQuery PredicateQuery(SelectionPredicate predicate) => new(new([PredicateOrders, Lines], []), new(PredicateOrders, "o"), new(PredicateOrders.PrimaryKey), [], predicate);
    private static ComparisonPredicate Comparison(SelectionComparison comparison, int value, string alias = "o") => new(new(alias, "id"), comparison, Value(value));
    private static SelectionParameterValue Value(int value) => new(typeof(int), value);
}
