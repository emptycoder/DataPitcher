using DataPitcher.Core.Selection;
using DataPitcher.Providers.PostgreSql;
using Xunit;
namespace DataPitcher.UnitTests.Selection;
public sealed class PostgreSqlSelectionSqlGeneratorTests
{
    [Fact]
    public void Compile_ProjectsOnlyDistinctRootStableKeysAndQuotesEveryIdentifier()
    {
        var query = SelectionQueryTestData.QuotedRootAndJoin(); var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);
        Assert.Equal(query.Root.Table, sql.RootTable); Assert.Equal(query.RootStableKey.Constraint, sql.RootStableKey);
        Assert.StartsWith("SELECT DISTINCT \"r\".\"Id\" AS \"__datapitcher_key_0\" FROM \"sales\".\"Order\"\"Rows\" AS \"r\" INNER JOIN", sql.CommandText);
        Assert.Contains("\"r\".\"CustomerId\" = \"c\".\"Id\"", sql.CommandText); Assert.Contains("\"c\".\"RegionId\" = \"g\".\"Id\"", sql.CommandText); Assert.DoesNotContain("SELECT DISTINCT \"c\".\"Id\"", sql.CommandText);
    }
    [Fact]
    public void Compile_NeverInlinesSqlMetacharactersAndEscapesLikeAsAParameter()
    {
        const string attack = "x%' OR 1=1; DROP TABLE orders; --"; var query = SelectionQueryTestData.Query(new TextPredicate(new("o", "Name"), TextMatch.Contains, new(typeof(string), attack)));
        var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);
        Assert.DoesNotContain(attack, sql.CommandText); Assert.Equal("@p0", sql.Parameters[0].Name); Assert.Equal(typeof(string), sql.Parameters[0].ClrType); Assert.Equal("x\\%' OR 1=1; DROP TABLE orders; --", sql.Parameters[0].Value); Assert.Contains("LIKE ('%' || @p0 || '%') ESCAPE '\\'", sql.CommandText);
    }
    [Fact]
    public void Compile_UsesPlaceholdersForEveryOperatorValue()
    {
        var generator = new PostgreSqlSelectionSqlGenerator();
        foreach (var query in SelectionQueryTestData.OperatorQueries()) { var sql = generator.Compile(query); Assert.All(sql.Parameters, parameter => Assert.Contains(parameter.Name, sql.CommandText)); }
    }
    [Fact]
    public void Compile_ReversesKnownRelationshipColumns()
    {
        var parent = new DataPitcher.Core.Schema.TableDefinition("sales", "Parents", [new("Id", typeof(int), false)], new("PK_Parents", ["Id"]), []); var child = new DataPitcher.Core.Schema.TableDefinition("sales", "Children", [new("Id", typeof(int), false), new("ParentId", typeof(int), false)], new("PK_Children", ["Id"]), []); var fk = new DataPitcher.Core.Schema.ForeignKeyDefinition("FK_Children_Parents", child, parent, ["ParentId"], ["Id"], true, true);
        var query = new SelectionQuery(new([parent, child], [fk]), new(parent, "p"), new(parent.PrimaryKey), [new ForeignKeyJoin("p", "c", fk, RelationshipDirection.Reverse)], null);
        Assert.Contains("\"p\".\"Id\" = \"c\".\"ParentId\"", new PostgreSqlSelectionSqlGenerator().Compile(query).CommandText);
    }
    [Fact]
    public void RandomlyEquivalentQueries_ProduceIdenticalSqlAndTypedParameterLists()
    {
        var random = new Random(20260902); var generator = new PostgreSqlSelectionSqlGenerator(); var expected = generator.Compile(SelectionQueryTestData.Query(new AndPredicate([SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(2), SelectionQueryTestData.Id(3)])));
        for (var i = 0; i < 128; i++) { var terms = new[] { SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(2), SelectionQueryTestData.Id(3) }.OrderBy(_ => random.Next()).ToArray(); var actual = generator.Compile(SelectionQueryTestData.Query(new AndPredicate(terms))); Assert.Equal(expected.CommandText, actual.CommandText); Assert.Equal(expected.Parameters, actual.Parameters); }
    }
    [Fact]
    public void Compile_ProducesIdenticalSqlUnderNonInvariantCultures()
    {
        var query = SelectionQueryTestData.Query(new TemporalRangePredicate(new("o", "Day"), TemporalKind.Date, new(typeof(DateOnly), new DateOnly(2026, 9, 2)), new(typeof(DateOnly), new DateOnly(2026, 9, 3))));
        var generator = new PostgreSqlSelectionSqlGenerator();
        var invariant = generator.Compile(query).CommandText;
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            var turkish = generator.Compile(query).CommandText;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
            var swedish = generator.Compile(query).CommandText;
            Assert.Equal(invariant, turkish); Assert.Equal(invariant, swedish);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }
    [Theory]
    [InlineData(TextMatch.Contains, "LIKE ('%' || @p0 || '%') ESCAPE '\\'")]
    [InlineData(TextMatch.StartsWith, "LIKE (@p0 || '%') ESCAPE '\\'")]
    [InlineData(TextMatch.EndsWith, "LIKE ('%' || @p0) ESCAPE '\\'")]
    public void Compile_UsesTheCorrectLikePattern(TextMatch match, string pattern)
    {
        var sql = new PostgreSqlSelectionSqlGenerator().Compile(SelectionQueryTestData.Query(new TextPredicate(new("o", "Name"), match, new(typeof(string), "value"))));
        Assert.Contains(pattern, sql.CommandText); Assert.Equal("value", sql.Parameters[0].Value);
    }
    [Fact]
    public void Compile_ProjectsCompositeKeysAndPairsEveryManualJoinColumn()
    {
        var orders = new DataPitcher.Core.Schema.TableDefinition("sales", "Orders", [new("TenantId", typeof(int), false), new("Id", typeof(int), false), new("CustomerTenantId", typeof(int), false), new("CustomerId", typeof(int), false)], new("PK_Orders", ["TenantId", "Id"]), []);
        var customers = new DataPitcher.Core.Schema.TableDefinition("sales", "Customers", [new("TenantId", typeof(int), false), new("Id", typeof(int), false)], new("PK_Customers", ["TenantId", "Id"]), []);
        var query = new SelectionQuery(new([orders, customers], []), new(orders, "o"), new(orders.PrimaryKey), [new ManualJoin("o", "c", customers, [new("CustomerTenantId", "TenantId"), new("CustomerId", "Id")])], null);
        var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);
        Assert.StartsWith("SELECT DISTINCT \"o\".\"TenantId\" AS \"__datapitcher_key_0\", \"o\".\"Id\" AS \"__datapitcher_key_1\"", sql.CommandText); Assert.Contains("\"o\".\"CustomerTenantId\" = \"c\".\"TenantId\" AND \"o\".\"CustomerId\" = \"c\".\"Id\"", sql.CommandText);
    }
    [Fact]
    public void Compile_WritesEverySetValueAsATypedParameter()
    {
        var query = SelectionQueryTestData.Query(new SetPredicate(new("o", "Id"), false, [new(typeof(int), 3), new(typeof(int), 1), new(typeof(int), 2)]));
        var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);
        Assert.Contains("\"o\".\"Id\" IN (@p0, @p1, @p2)", sql.CommandText); Assert.Equal([1, 2, 3], sql.Parameters.Select(x => (int)x.Value));
    }
    [Fact]
    public void Compile_PreservesBooleanGroupingAndNegation()
    {
        var query = SelectionQueryTestData.Query(new NotPredicate(new OrPredicate([SelectionQueryTestData.Id(3), new AndPredicate([SelectionQueryTestData.Id(1), new ComparisonPredicate(new("o", "Id"), SelectionComparison.GreaterThan, new(typeof(int), 2))])])));
        var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);
        Assert.Equal("SELECT DISTINCT \"o\".\"Id\" AS \"__datapitcher_key_0\" FROM \"sales\".\"Orders\" AS \"o\" WHERE NOT (((\"o\".\"Id\" = @p0 AND \"o\".\"Id\" > @p1) OR \"o\".\"Id\" = @p2))", sql.CommandText); Assert.Equal([1, 2, 3], sql.Parameters.Select(x => (int)x.Value));
    }
    [Fact]
    public void Compile_RendersNestedNotExistsPredicate()
    {
        var orders = new DataPitcher.Core.Schema.TableDefinition("sales", "Orders", [new("Id", typeof(int), false)], new("PK_Orders", ["Id"]), []);
        var lines = new DataPitcher.Core.Schema.TableDefinition("sales", "Lines", [new("Id", typeof(int), false), new("OrderId", typeof(int), false)], new("PK_Lines", ["Id"]), []);
        var query = new SelectionQuery(new([orders, lines], []), new(orders, "o"), new(orders.PrimaryKey), [], new ExistsPredicate(lines, "l", [new(new("o", "Id"), "OrderId")], new SetPredicate(new("l", "Id"), false, [new(typeof(int), 2), new(typeof(int), 1)]), true));
        var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM \"sales\".\"Lines\" AS \"l\" WHERE \"o\".\"Id\" = \"l\".\"OrderId\" AND \"l\".\"Id\" IN (@p0, @p1))", sql.CommandText); Assert.Equal([1, 2], sql.Parameters.Select(x => (int)x.Value));
    }
    [Fact]
    public void Compile_WritesEveryExistsCorrelation()
    {
        var orders = new DataPitcher.Core.Schema.TableDefinition("sales", "Orders", [new("TenantId", typeof(int), false), new("Id", typeof(int), false)], new("PK_Orders", ["TenantId", "Id"]), []);
        var lines = new DataPitcher.Core.Schema.TableDefinition("sales", "Lines", [new("OrderTenantId", typeof(int), false), new("OrderId", typeof(int), false)], new("PK_Lines", ["OrderTenantId", "OrderId"]), []);
        var query = new SelectionQuery(new([orders, lines], []), new(orders, "o"), new(orders.PrimaryKey), [], new ExistsPredicate(lines, "l", [new(new("o", "TenantId"), "OrderTenantId"), new(new("o", "Id"), "OrderId")], null, false));
        var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);
        Assert.Contains("\"o\".\"TenantId\" = \"l\".\"OrderTenantId\" AND \"o\".\"Id\" = \"l\".\"OrderId\"", sql.CommandText);
    }
}
