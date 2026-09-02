using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Providers.SqlServer;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerSelectionExecutionTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task ReadKeysAsync_WhenOneOrderJoinsFiveLines_ReturnsOneDistinctRootKeyAndLeavesTheTargetEmpty()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("INSERT dbo.customers VALUES (1,N'c'); INSERT dbo.orders VALUES (10,1); INSERT dbo.order_lines VALUES (1,10),(2,10),(3,10),(4,10),(5,10);");
        var schema = await SchemaAsync(scope);

        var keys = await Executor(scope, schema).ReadKeysAsync(OrdersWithLines(schema), 100, CancellationToken.None);

        Assert.Single(keys.Keys, key => key == new StableKey([new("order_id", 10)]));
        Assert.Equal(0, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.orders"));
    }

    [Fact]
    public async Task CountAsync_WhenOneOrderJoinsFiveLines_CountsOneDistinctRootKey()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("INSERT dbo.customers VALUES (1,N'c'); INSERT dbo.orders VALUES (10,1); INSERT dbo.order_lines VALUES (1,10),(2,10),(3,10),(4,10),(5,10);");
        var schema = await SchemaAsync(scope);

        var count = await Executor(scope, schema).CountAsync(OrdersWithLines(schema), CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PreviewAsync_EnforcesTheProvidedBoundAndTruncatesOnlyPreviewValues()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("CREATE TABLE dbo.preview_orders (id int PRIMARY KEY, customer_id int REFERENCES dbo.customers(customer_id), note nvarchar(max) NOT NULL, payload varbinary(max) NOT NULL, calculated AS id + 1); INSERT dbo.customers VALUES (1,N'c'); INSERT dbo.preview_orders(id,customer_id,note,payload) VALUES " + string.Join(",", Enumerable.Range(1, 201).Select(id => "(" + id + ",1,REPLICATE(N'x',300),CONVERT(varbinary(max),REPLICATE('a',300)))")));
        var schema = await SchemaAsync(scope);
        var table = schema.Table("preview_orders").Definition;

        var preview = await Executor(scope, schema).PreviewAsync(Raw(table, "SELECT id AS [__datapitcher_key_0] FROM dbo.preview_orders"), 200, 256, 256, CancellationToken.None);

        Assert.Equal(200, preview.Rows.Count);
        Assert.Single(preview.Columns, column => column.Name == "id" && column.IsStableKey);
        Assert.Single(preview.Columns, column => column.Name == "customer_id" && column.IsForeignKey);
        Assert.Single(preview.Columns, column => column.Name == "calculated" && column.IsGenerated);
        Assert.True(preview.Rows[0].Values["note"].IsTruncated);
        Assert.Equal(256, ((string)preview.Rows[0].Values["note"].Value!).Length);
        Assert.True(preview.Rows[0].Values["payload"].IsTruncated);
        Assert.Equal(256, ((byte[])preview.Rows[0].Values["payload"].Value!).Length);
        Assert.Equal(201, await scope.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.preview_orders"));
    }

    [Fact]
    public async Task PreviewAsync_WhenAColumnIsNull_PreservesTheNullWithoutMarkingItTruncated()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("CREATE TABLE dbo.nullable_preview_orders (id int PRIMARY KEY, note nvarchar(max) NULL); INSERT dbo.nullable_preview_orders VALUES (1,NULL);");
        var schema = await SchemaAsync(scope);
        var table = schema.Table("nullable_preview_orders").Definition;

        var preview = await Executor(scope, schema).PreviewAsync(Raw(table, "SELECT id AS [__datapitcher_key_0] FROM dbo.nullable_preview_orders"), 1, 256, 256, CancellationToken.None);

        var cell = Assert.Single(preview.Rows).Values["note"];
        Assert.Null(cell.Value);
        Assert.False(cell.IsTruncated);
    }

    [Fact]
    public async Task ReadKeysAsync_WhenMoreThanTheMaximumWouldBeReturned_ThrowsInsteadOfReturningAPartialSet()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("INSERT dbo.customers VALUES (1,N'c'); INSERT dbo.orders VALUES (10,1),(11,1); INSERT dbo.order_lines VALUES (1,10),(2,11);");
        var schema = await SchemaAsync(scope);

        await Assert.ThrowsAsync<SelectionResultLimitExceededException>(() => Executor(scope, schema).ReadKeysAsync(OrdersWithLines(schema), 1, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_WhenRawCteProjectsEveryKeyAlias_AcceptsIt()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var schema = await SchemaAsync(scope);
        var orders = schema.Table("orders").Definition;

        await Executor(scope, schema).ValidateAsync(Raw(orders, "WITH roots AS (SELECT @value AS [__datapitcher_key_0]) SELECT [__datapitcher_key_0] FROM roots", [new("@value", typeof(int), 7)]), CancellationToken.None);
    }

    [Fact]
    public async Task ValidateAsync_WhenRawSqlOmitsAStableKeyAlias_RejectsTheResultShape()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var schema = await SchemaAsync(scope);
        var orders = schema.Table("orders").Definition;

        await Assert.ThrowsAsync<RawSqlValidationException>(() => Executor(scope, schema).ValidateAsync(Raw(orders, "SELECT 7 AS not_a_key"), CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_WhenRawSqlProjectsAnExtraColumn_RejectsTheResultShape()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var schema = await SchemaAsync(scope);
        var orders = schema.Table("orders").Definition;

        await Assert.ThrowsAsync<RawSqlValidationException>(() => Executor(scope, schema).ValidateAsync(Raw(orders, "SELECT 7 AS [__datapitcher_key_0], 8 AS extra"), CancellationToken.None));
    }

    [Theory]
    [InlineData("DELETE FROM dbo.orders", "Raw SQL must start with SELECT or WITH.")]
    [InlineData("SELECT 7 AS [__datapitcher_key_0]; SELECT 8 AS [__datapitcher_key_0]", "Raw SQL may contain only one statement.")]
    [InlineData("SELECT 7 AS [__datapitcher_key_0]\nGO", "SQL Server batch separators are not allowed.")]
    public async Task ValidateAsync_WhenRawSqlIsUnsafe_RejectsIt(string sql, string message)
    {
        await using var scope = await fixture.CreateScopeAsync();
        var schema = await SchemaAsync(scope);
        var orders = schema.Table("orders").Definition;

        var error = await Assert.ThrowsAsync<RawSqlValidationException>(() => Executor(scope, schema).ValidateAsync(Raw(orders, sql), CancellationToken.None));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task Operations_WithPreCancelledToken_PropagateCancellationToEveryDatabaseOperation()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var schema = await SchemaAsync(scope);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(scope, schema).ValidateAsync(OrdersWithLines(schema), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(scope, schema).ReadKeysAsync(OrdersWithLines(schema), 100, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(scope, schema).PreviewAsync(OrdersWithLines(schema), 200, 256, 256, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(scope, schema).CountAsync(OrdersWithLines(schema), cancelled.Token));
    }

    [Fact]
    public async Task RawSqlWithTrailingOrderingAndTerminator_WorksThroughEveryDerivedTableWrapper()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("INSERT dbo.customers VALUES (1,N'c'); INSERT dbo.orders VALUES (10,1);");
        var schema = await SchemaAsync(scope);
        var orders = schema.Table("orders").Definition;
        var raw = Raw(orders, "SELECT order_id AS [__datapitcher_key_0] FROM dbo.orders ORDER BY order_id;");
        var executor = Executor(scope, schema);

        await executor.ValidateAsync(raw, CancellationToken.None);
        Assert.Single((await executor.ReadKeysAsync(raw, 100, CancellationToken.None)).Keys, key => key == new StableKey([new("order_id", 10)]));
        Assert.Single((await executor.PreviewAsync(raw, 200, 256, 256, CancellationToken.None)).Rows);
        Assert.Equal(1, await executor.CountAsync(raw, CancellationToken.None));
    }

    [Fact]
    public async Task PreviewAsync_WhenRawSqlStartsWithACte_ReturnsItsSelectedRows()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("INSERT dbo.customers VALUES (1,N'c'); INSERT dbo.orders VALUES (10,1);");
        var schema = await SchemaAsync(scope);
        var orders = schema.Table("orders").Definition;

        var preview = await Executor(scope, schema).PreviewAsync(Raw(orders, "WITH roots AS (SELECT order_id AS [__datapitcher_key_0] FROM dbo.orders) SELECT [__datapitcher_key_0] FROM roots"), 1, 256, 256, CancellationToken.None);

        Assert.Single(preview.Rows, row => row.StableKey == new StableKey([new("order_id", 10)]));
    }

    [Fact]
    public async Task RawSqlWithQuotedAndCommentedOrdering_IsNotLexicallyRemoved()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("INSERT dbo.customers VALUES (1,N'c'); INSERT dbo.orders VALUES (10,1);");
        var schema = await SchemaAsync(scope);
        var orders = schema.Table("orders").Definition;
        var raw = Raw(orders, "SELECT order_id AS [__datapitcher_key_0] FROM dbo.orders WHERE 'ORDER BY ;' = 'ORDER BY ;' /* ORDER BY ; */");

        Assert.Single((await Executor(scope, schema).ReadKeysAsync(raw, 100, CancellationToken.None)).Keys, key => key == new StableKey([new("order_id", 10)]));
    }

    private static SqlServerSelectionExecutor Executor(SqlServerClosureScope scope, SqlServerSchemaSnapshot schema) => new(scope.SourceConnectionString, schema);
    private static async Task<SqlServerSchemaSnapshot> SchemaAsync(SqlServerClosureScope scope) => await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None);

    private static GeneratedSelectionSql OrdersWithLines(SqlServerSchemaSnapshot schema)
    {
        var orders = schema.Table("orders").Definition;
        var lines = schema.Table("order_lines").Definition;
        var query = new SelectionQuery(new([orders, lines], []), new(orders, "o"), new(orders.PrimaryKey), [new ManualJoin("o", "l", lines, [new("order_id", "order_id")])], null);
        return new SqlServerSelectionSqlGenerator().Compile(query);
    }

    private static GeneratedSelectionSql Raw(TableDefinition table, string sql, IEnumerable<SelectionSqlParameter>? parameters = null) => new(sql, table, table.PrimaryKey!, parameters ?? [], true);
}
