using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Providers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlSelectionSqlGeneratorCoverageTests
{
    [Fact]
    public void Compile_WithoutJoinsOrPredicate_ProjectsEveryCompositeStableKey()
    {
        var orders = new TableDefinition(
            "sales",
            "orders",
            [new("tenant_id", typeof(int), false), new("id", typeof(int), false)],
            new("pk_orders", ["tenant_id", "id"]),
            []
        );
        var sql = new PostgreSqlSelectionSqlGenerator().Compile(
            new SelectionQuery(new([orders], []), new(orders, "o"), new(orders.PrimaryKey), [], null)
        );

        Assert.Equal(
            "SELECT DISTINCT \"o\".\"tenant_id\" AS \"__datapitcher_key_0\", \"o\".\"id\" AS \"__datapitcher_key_1\" FROM \"sales\".\"orders\" AS \"o\"",
            sql.CommandText
        );
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Compile_ForwardForeignKeyJoin_PairsEachChildAndParentColumn()
    {
        var parent = new TableDefinition(
            "sales",
            "parents",
            [new("tenant_id", typeof(int), false), new("id", typeof(int), false)],
            new("pk_parents", ["tenant_id", "id"]),
            []
        );
        var child = new TableDefinition(
            "sales",
            "children",
            [
                new("id", typeof(int), false),
                new("parent_tenant_id", typeof(int), false),
                new("parent_id", typeof(int), false),
            ],
            new("pk_children", ["id"]),
            []
        );
        var foreignKey = new ForeignKeyDefinition(
            "fk_children_parents",
            child,
            parent,
            ["parent_tenant_id", "parent_id"],
            ["tenant_id", "id"],
            true,
            true
        );
        var query = new SelectionQuery(
            new([child, parent], [foreignKey]),
            new(child, "c"),
            new(child.PrimaryKey),
            [new ForeignKeyJoin("c", "p", foreignKey, RelationshipDirection.Forward)],
            null
        );

        var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);

        Assert.Contains(
            "\"c\".\"parent_tenant_id\" = \"p\".\"tenant_id\" AND \"c\".\"parent_id\" = \"p\".\"id\"",
            sql.CommandText,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Compile_ReverseForeignKeyJoin_PairsEachParentAndChildColumn()
    {
        var parent = new TableDefinition(
            "sales",
            "parents",
            [new("tenant_id", typeof(int), false), new("id", typeof(int), false)],
            new("pk_parents", ["tenant_id", "id"]),
            []
        );
        var child = new TableDefinition(
            "sales",
            "children",
            [
                new("id", typeof(int), false),
                new("parent_tenant_id", typeof(int), false),
                new("parent_id", typeof(int), false),
            ],
            new("pk_children", ["id"]),
            []
        );
        var foreignKey = new ForeignKeyDefinition(
            "fk_children_parents",
            child,
            parent,
            ["parent_tenant_id", "parent_id"],
            ["tenant_id", "id"],
            true,
            true
        );
        var query = new SelectionQuery(
            new([child, parent], [foreignKey]),
            new(parent, "p"),
            new(parent.PrimaryKey),
            [new ForeignKeyJoin("p", "c", foreignKey, RelationshipDirection.Reverse)],
            null
        );

        var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);

        Assert.Contains(
            "\"p\".\"tenant_id\" = \"c\".\"parent_tenant_id\" AND \"p\".\"id\" = \"c\".\"parent_id\"",
            sql.CommandText,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Compile_ManualJoin_PairsEverySpecifiedColumn()
    {
        var orders = new TableDefinition(
            "sales",
            "orders",
            [
                new("tenant_id", typeof(int), false),
                new("id", typeof(int), false),
                new("customer_tenant_id", typeof(int), false),
                new("customer_id", typeof(int), false),
            ],
            new("pk_orders", ["tenant_id", "id"]),
            []
        );
        var customers = new TableDefinition(
            "sales",
            "customers",
            [new("tenant_id", typeof(int), false), new("id", typeof(int), false)],
            new("pk_customers", ["tenant_id", "id"]),
            []
        );
        var query = new SelectionQuery(
            new([orders, customers], []),
            new(orders, "o"),
            new(orders.PrimaryKey),
            [new ManualJoin("o", "c", customers, [new("customer_tenant_id", "tenant_id"), new("customer_id", "id")])],
            null
        );

        var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);

        Assert.Contains(
            "\"o\".\"customer_tenant_id\" = \"c\".\"tenant_id\" AND \"o\".\"customer_id\" = \"c\".\"id\"",
            sql.CommandText,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [MemberData(nameof(PredicateCases))]
    public void Compile_RendersEachValidatedPredicateAsParameterizedSql(
        SelectionPredicate predicate,
        string expectedSql,
        object[] expectedValues
    )
    {
        var sql = new PostgreSqlSelectionSqlGenerator().Compile(Query(predicate));

        Assert.Contains(expectedSql, sql.CommandText, StringComparison.Ordinal);
        Assert.Equal(expectedValues, sql.Parameters.Select(parameter => parameter.Value));
        Assert.All(
            sql.Parameters,
            parameter => Assert.Contains(parameter.Name, sql.CommandText, StringComparison.Ordinal)
        );
    }

    [Fact]
    public void StrictExactBlockedException_PreservesTheRefusalReason()
    {
        var error = new PostgreSqlStrictExactBlockedException("StrictExact is blocked by a target rewrite rule.");

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal("StrictExact is blocked by a target rewrite rule.", error.Message);
    }

    [Fact]
    public void StableKeyCodec_Encode_WhenBigintValueHasTheWrongClrType_RejectsTheKey()
    {
        var table = new PostgreSqlWriteTable(
            new("sales", "orders"),
            [new("id", "bigint", NpgsqlTypes.NpgsqlDbType.Bigint, true, false, false, false, null)]
        );

        Assert.Throws<NotSupportedException>(() =>
            PostgreSqlStableKeyCodec.Encode(new DataPitcher.Core.Identity.StableKey([new("id", 1)]), table)
        );
    }

    public static IEnumerable<object?[]> PredicateCases()
    {
        yield return
        [
            new AndPredicate([
                Comparison(SelectionComparison.Equal, 1),
                Comparison(SelectionComparison.GreaterThan, 2),
            ]),
            "\"o\".\"id\" = @p0 AND \"o\".\"id\" > @p1",
            new object[] { 1, 2 },
        ];
        yield return
        [
            new OrPredicate([Comparison(SelectionComparison.Equal, 1), Comparison(SelectionComparison.GreaterThan, 2)]),
            "\"o\".\"id\" = @p0 OR \"o\".\"id\" > @p1",
            new object[] { 1, 2 },
        ];
        yield return
        [
            new NotPredicate(Comparison(SelectionComparison.NotEqual, 1)),
            "NOT (\"o\".\"id\" <> @p0)",
            new object[] { 1 },
        ];
        yield return [Comparison(SelectionComparison.Equal, 1), "\"o\".\"id\" = @p0", new object[] { 1 }];
        yield return [Comparison(SelectionComparison.NotEqual, 1), "\"o\".\"id\" <> @p0", new object[] { 1 }];
        yield return [Comparison(SelectionComparison.GreaterThan, 1), "\"o\".\"id\" > @p0", new object[] { 1 }];
        yield return [Comparison(SelectionComparison.GreaterOrEqual, 1), "\"o\".\"id\" >= @p0", new object[] { 1 }];
        yield return [Comparison(SelectionComparison.LessThan, 1), "\"o\".\"id\" < @p0", new object[] { 1 }];
        yield return [Comparison(SelectionComparison.LessOrEqual, 1), "\"o\".\"id\" <= @p0", new object[] { 1 }];
        yield return
        [
            new BetweenPredicate(new("o", "id"), Value(1), Value(2)),
            "\"o\".\"id\" BETWEEN @p0 AND @p1",
            new object[] { 1, 2 },
        ];
        yield return
        [
            new SetPredicate(new("o", "id"), false, [Value(2), Value(1)]),
            "\"o\".\"id\" IN (@p0, @p1)",
            new object[] { 1, 2 },
        ];
        yield return
        [
            new SetPredicate(new("o", "id"), true, [Value(2), Value(1)]),
            "\"o\".\"id\" NOT IN (@p0, @p1)",
            new object[] { 1, 2 },
        ];
        yield return [new NullPredicate(new("o", "name"), false), "\"o\".\"name\" IS NULL", Array.Empty<object>()];
        yield return [new NullPredicate(new("o", "name"), true), "\"o\".\"name\" IS NOT NULL", Array.Empty<object>()];
        yield return
        [
            new TextPredicate(new("o", "name"), TextMatch.Contains, new(typeof(string), "a%_\\")),
            "LIKE ('%' || @p0 || '%') ESCAPE '\\'",
            new object[] { "a\\%\\_\\\\" },
        ];
        yield return
        [
            new TextPredicate(new("o", "name"), TextMatch.StartsWith, new(typeof(string), "value")),
            "LIKE (@p0 || '%') ESCAPE '\\'",
            new object[] { "value" },
        ];
        yield return
        [
            new TextPredicate(new("o", "name"), TextMatch.EndsWith, new(typeof(string), "value")),
            "LIKE ('%' || @p0) ESCAPE '\\'",
            new object[] { "value" },
        ];
        yield return
        [
            new BooleanPredicate(new("o", "active"), new(typeof(bool), true)),
            "\"o\".\"active\" = @p0",
            new object[] { true },
        ];
        yield return
        [
            new TemporalRangePredicate(
                new("o", "day"),
                TemporalKind.Date,
                new(typeof(DateOnly), new DateOnly(2026, 9, 2)),
                new(typeof(DateOnly), new DateOnly(2026, 9, 3))
            ),
            "\"o\".\"day\" BETWEEN @p0 AND @p1",
            new object[] { new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 3) },
        ];
        yield return
        [
            new TemporalRangePredicate(
                new("o", "at"),
                TemporalKind.Time,
                new(typeof(TimeOnly), new TimeOnly(9, 0)),
                new(typeof(TimeOnly), new TimeOnly(10, 0))
            ),
            "\"o\".\"at\" BETWEEN @p0 AND @p1",
            new object[] { new TimeOnly(9, 0), new TimeOnly(10, 0) },
        ];
        yield return
        [
            new TemporalRangePredicate(
                new("o", "occurred"),
                TemporalKind.DateTime,
                new(typeof(DateTime), new DateTime(2026, 9, 2)),
                new(typeof(DateTime), new DateTime(2026, 9, 3))
            ),
            "\"o\".\"occurred\" BETWEEN @p0 AND @p1",
            new object[] { new DateTime(2026, 9, 2), new DateTime(2026, 9, 3) },
        ];
        yield return
        [
            new ExistsPredicate(
                Lines,
                "l",
                [new(new("o", "tenant_id"), "order_tenant_id"), new(new("o", "id"), "order_id")],
                null,
                false
            ),
            "EXISTS (SELECT 1 FROM \"sales\".\"lines\" AS \"l\" WHERE \"o\".\"tenant_id\" = \"l\".\"order_tenant_id\" AND \"o\".\"id\" = \"l\".\"order_id\")",
            Array.Empty<object>(),
        ];
        yield return
        [
            new ExistsPredicate(
                Lines,
                "l",
                [new(new("o", "id"), "order_id")],
                Comparison(SelectionComparison.Equal, 7, "l"),
                true
            ),
            "NOT EXISTS (SELECT 1 FROM \"sales\".\"lines\" AS \"l\" WHERE \"o\".\"id\" = \"l\".\"order_id\" AND \"l\".\"id\" = @p0)",
            new object[] { 7 },
        ];
    }

    private static readonly TableDefinition Orders = new(
        "sales",
        "orders",
        [
            new("tenant_id", typeof(int), false),
            new("id", typeof(int), false),
            new("name", typeof(string), true),
            new("active", typeof(bool), false),
            new("day", typeof(DateOnly), false),
            new("at", typeof(TimeOnly), false),
            new("occurred", typeof(DateTime), false),
        ],
        new("pk_orders", ["tenant_id", "id"]),
        []
    );
    private static readonly TableDefinition Lines = new(
        "sales",
        "lines",
        [
            new("id", typeof(int), false),
            new("order_tenant_id", typeof(int), false),
            new("order_id", typeof(int), false),
        ],
        new("pk_lines", ["id"]),
        []
    );

    private static SelectionQuery Query(SelectionPredicate predicate) =>
        new(new([Orders, Lines], []), new(Orders, "o"), new(Orders.PrimaryKey), [], predicate);

    private static ComparisonPredicate Comparison(SelectionComparison comparison, int value, string alias = "o") =>
        new(new(alias, "id"), comparison, Value(value));

    private static SelectionParameterValue Value(int value) => new(typeof(int), value);
}

public sealed class PostgreSqlTargetCheckpointStoreCoverageTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlTargetCheckpointStoreCoverageTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task InitializeAsync_WhenTheFenceUpdateIsCancelled_ThrowsFenceLost()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var store = new PostgreSqlTargetCheckpointStore(scope.Target);
        var stale = PostgreSqlTransferTestData.Context();
        await store.InitializeAsync(stale, CancellationToken.None);
        await scope.ExecuteTargetAsync(
            "CREATE FUNCTION datapitcher.cancel_checkpoint_fence_update() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NULL; END; $$; CREATE TRIGGER cancel_checkpoint_fence_update BEFORE UPDATE ON datapitcher.transfer_checkpoints FOR EACH ROW WHEN (NEW.fence_token > OLD.fence_token) EXECUTE FUNCTION datapitcher.cancel_checkpoint_fence_update();"
        );

        await Assert.ThrowsAsync<PostgreSqlFenceLostException>(() =>
            store.InitializeAsync(stale with { FenceToken = 2 }, CancellationToken.None)
        );

        Assert.Equal(1, (await store.ReadAsync(stale.JobId, stale.RunId, CancellationToken.None))!.FenceToken);
    }
}
