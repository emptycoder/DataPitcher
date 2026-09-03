using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Providers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlSelectionExecutionTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture fixture;

    public PostgreSqlSelectionExecutionTests(PostgreSqlClosureFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task ReadKeysAsync_WhenOneOrderHasFiveJoinedLines_ReturnsOneOrderKeyAndLeavesTheTargetEmpty()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "INSERT INTO customers VALUES (1,'c'); INSERT INTO orders VALUES (10,1); INSERT INTO order_lines VALUES (1,10),(2,10),(3,10),(4,10),(5,10);"
        );
        var schema = await SchemaAsync(scope);

        var keys = await new PostgreSqlSelectionExecutor(scope.Source, schema).ReadKeysAsync(
            OrdersWithLines(schema),
            100,
            CancellationToken.None
        );

        Assert.Equal("orders", keys.RootTable.Name);
        Assert.Single(keys.Keys, key => key == new StableKey([new("order_id", 10)]));
        Assert.Equal(0L, await scope.ScalarTargetAsync<long>("SELECT COUNT(*) FROM orders"));
    }

    [Fact]
    public async Task CountAsync_WhenOneOrderHasFiveJoinedLines_CountsOneDistinctOrder()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "INSERT INTO customers VALUES (1,'c'); INSERT INTO orders VALUES (10,1); INSERT INTO order_lines VALUES (1,10),(2,10),(3,10),(4,10),(5,10);"
        );
        var schema = await SchemaAsync(scope);

        var count = await new PostgreSqlSelectionExecutor(scope.Source, schema).CountAsync(
            OrdersWithLines(schema),
            CancellationToken.None
        );

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PreviewAsync_UsesTheProvidedBoundAndTruncatesOnlyPreviewValues()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE preview_orders (id integer PRIMARY KEY, customer_id integer REFERENCES customers(customer_id), note text NOT NULL, payload bytea NOT NULL, generated integer GENERATED ALWAYS AS (id + 1) STORED); INSERT INTO customers VALUES (1,'c'); INSERT INTO preview_orders(id,customer_id,note,payload) SELECT value,1,repeat('x',300),decode(repeat('ab',300),'hex') FROM generate_series(1,201) value;"
        );
        var schema = await SchemaAsync(scope);
        var table = schema.Table("preview_orders").Definition;

        var preview = await new PostgreSqlSelectionExecutor(scope.Source, schema).PreviewAsync(
            Raw(table, "SELECT id AS \"__datapitcher_key_0\" FROM preview_orders"),
            200,
            256,
            256,
            CancellationToken.None
        );

        Assert.Equal(200, preview.Rows.Count);
        Assert.Single(preview.Columns, column => column.Name == "id" && column.IsStableKey);
        Assert.Single(preview.Columns, column => column.Name == "customer_id" && column.IsForeignKey);
        Assert.Single(preview.Columns, column => column.Name == "generated" && column.IsGenerated);
        Assert.True(preview.Rows[0].Values["note"].IsTruncated);
        Assert.Equal(256, ((string)preview.Rows[0].Values["note"].Value!).Length);
        Assert.True(preview.Rows[0].Values["payload"].IsTruncated);
        Assert.Equal(256, ((byte[])preview.Rows[0].Values["payload"].Value!).Length);
        Assert.Equal(201L, await scope.ScalarAsync<long>("SELECT COUNT(*) FROM preview_orders"));
    }

    [Fact]
    public async Task PreviewAsync_WhenAColumnIsNull_PreservesTheNullWithoutMarkingItTruncated()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE nullable_preview_orders (id integer PRIMARY KEY, note text NULL); INSERT INTO nullable_preview_orders VALUES (1,NULL);"
        );
        var schema = await SchemaAsync(scope);
        var table = schema.Table("nullable_preview_orders").Definition;

        var preview = await new PostgreSqlSelectionExecutor(scope.Source, schema).PreviewAsync(
            Raw(table, "SELECT id AS \"__datapitcher_key_0\" FROM nullable_preview_orders"),
            1,
            256,
            256,
            CancellationToken.None
        );

        var cell = Assert.Single(preview.Rows).Values["note"];
        Assert.Null(cell.Value);
        Assert.False(cell.IsTruncated);
    }

    [Fact]
    public async Task PreviewAsync_UsesALimitInTheGeneratedServerSql()
    {
        var recorder = new PostgreSqlCommandRecorder();
        await using var scope = await fixture.CreateScopeAsync(sourceRecorder: recorder);
        await scope.ExecuteAsync("INSERT INTO customers VALUES (1,'c'); INSERT INTO orders VALUES (10,1);");
        var schema = await SchemaAsync(scope);

        await new PostgreSqlSelectionExecutor(scope.Source, schema).PreviewAsync(
            OrdersWithLines(schema),
            SelectionExecutionLimits.PreviewRowLimit,
            256,
            256,
            CancellationToken.None
        );

        Assert.True(recorder.AnyContains("LIMIT $"));
    }

    [Fact]
    public async Task ReadKeysAsync_WhenMoreThanTheMaximumWouldBeReturned_ThrowsInsteadOfReturningAPartialSet()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "INSERT INTO customers VALUES (1,'c'); INSERT INTO orders VALUES (10,1),(11,1); INSERT INTO order_lines VALUES (1,10),(2,11);"
        );
        var schema = await SchemaAsync(scope);

        await Assert.ThrowsAsync<SelectionResultLimitExceededException>(() =>
            new PostgreSqlSelectionExecutor(scope.Source, schema).ReadKeysAsync(
                OrdersWithLines(schema),
                1,
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task ValidateAsync_WhenRawCteProjectsEveryKeyAlias_AcceptsIt()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var schema = await SchemaAsync(scope);
        var orders = schema.Table("orders").Definition;

        await new PostgreSqlSelectionExecutor(scope.Source, schema).ValidateAsync(
            Raw(
                orders,
                "WITH roots AS (SELECT @value AS \"__datapitcher_key_0\") SELECT \"__datapitcher_key_0\" FROM roots",
                [new("@value", typeof(int), 7)]
            ),
            CancellationToken.None
        );
    }

    [Fact]
    public async Task ValidateAsync_WhenRawSqlOmitsAStableKeyAlias_RejectsTheResultShape()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var schema = await SchemaAsync(scope);
        var orders = schema.Table("orders").Definition;

        await Assert.ThrowsAsync<RawSqlValidationException>(() =>
            new PostgreSqlSelectionExecutor(scope.Source, schema).ValidateAsync(
                Raw(orders, "SELECT 7 AS not_a_key"),
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task ValidateAsync_WhenRawSqlProjectsAnExtraColumn_RejectsTheResultShape()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var schema = await SchemaAsync(scope);
        var orders = schema.Table("orders").Definition;

        await Assert.ThrowsAsync<RawSqlValidationException>(() =>
            new PostgreSqlSelectionExecutor(scope.Source, schema).ValidateAsync(
                Raw(orders, "SELECT 7 AS \"__datapitcher_key_0\", 8 AS extra"),
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task RawSqlWithTrailingTerminator_WorksThroughEveryDerivedTableWrapper()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("INSERT INTO customers VALUES (1,'c'); INSERT INTO orders VALUES (10,1);");
        var schema = await SchemaAsync(scope);
        var orders = schema.Table("orders").Definition;
        var raw = Raw(orders, "SELECT order_id AS \"__datapitcher_key_0\" FROM orders;");
        var executor = new PostgreSqlSelectionExecutor(scope.Source, schema);

        await executor.ValidateAsync(raw, CancellationToken.None);
        Assert.Single(
            (await executor.ReadKeysAsync(raw, 100, CancellationToken.None)).Keys,
            key => key == new StableKey([new("order_id", 10)])
        );
        Assert.Single((await executor.PreviewAsync(raw, 200, 256, 256, CancellationToken.None)).Rows);
        Assert.Equal(1, await executor.CountAsync(raw, CancellationToken.None));
    }

    [Fact]
    public async Task Operations_WithPreCancelledToken_PropagateCancellationToEveryDatabaseOperation()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var schema = await SchemaAsync(scope);
        var executor = new PostgreSqlSelectionExecutor(scope.Source, schema);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ValidateAsync(OrdersWithLines(schema), cancelled.Token)
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ReadKeysAsync(OrdersWithLines(schema), 100, cancelled.Token)
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.PreviewAsync(OrdersWithLines(schema), 200, 256, 256, cancelled.Token)
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.CountAsync(OrdersWithLines(schema), cancelled.Token)
        );
    }

    private static async Task<PostgreSqlSchemaSnapshot> SchemaAsync(PostgreSqlClosureScope scope) =>
        await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);

    private static GeneratedSelectionSql OrdersWithLines(PostgreSqlSchemaSnapshot schema)
    {
        var orders = schema.Table("orders").Definition;
        var lines = schema.Table("order_lines").Definition;
        var query = new SelectionQuery(
            new([orders, lines], []),
            new(orders, "o"),
            new(orders.PrimaryKey),
            [new ManualJoin("o", "l", lines, [new("order_id", "order_id")])],
            null
        );
        return new PostgreSqlSelectionSqlGenerator().Compile(query);
    }

    private static GeneratedSelectionSql Raw(
        TableDefinition table,
        string sql,
        IEnumerable<SelectionSqlParameter>? parameters = null
    ) => new(sql, table, table.PrimaryKey!, parameters ?? [], true);
}
