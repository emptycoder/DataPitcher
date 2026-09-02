using DataPitcher.Core.Selection;
using DataPitcher.PostgreSql;
using Xunit;
namespace DataPitcher.UnitTests.Selection;
public sealed class PostgreSqlSelectionSqlGeneratorTests
{
    [Fact]
    public void Compile_ProjectsOnlyDistinctRootStableKeysAndQuotesEveryIdentifier()
    {
        var query = SelectionQueryTestData.QuotedRootAndJoin(); var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);
        Assert.Equal(query.Root.Table, sql.RootTable); Assert.Equal(query.RootStableKey.Constraint, sql.RootStableKey);
        Assert.StartsWith("SELECT DISTINCT \"r\".\"Id\" FROM \"sales\".\"Order\"\"Rows\" AS \"r\" INNER JOIN", sql.CommandText);
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
}
