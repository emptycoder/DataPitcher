using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerTransferSchemaReaderTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task ReadAsync_WhenTheNameIsAViewRatherThanABaseTable_FailsNamingTheTableAndTheVisibilityRule()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.reader_rows (id int NOT NULL CONSTRAINT PK_reader_rows PRIMARY KEY, name nvarchar(32) NULL);"
                + " EXEC('CREATE VIEW dbo.reader_view AS SELECT id, name FROM dbo.reader_rows');"
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqlServerTransferSchemaReader(scope.TargetConnectionString).ReadAsync(
                "dbo",
                "reader_view",
                ["id"],
                CancellationToken.None
            )
        );

        Assert.Contains("dbo.reader_view", exception.Message);
        Assert.Contains("base table", exception.Message);
        Assert.Contains("permission", exception.Message);
        Assert.DoesNotContain("Password", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_WhenAStableKeyColumnIsNotOnTheTable_FailsNamingTheTableAndTheColumn()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.reader_rows (id int NOT NULL CONSTRAINT PK_reader_rows PRIMARY KEY, name nvarchar(32) NULL);"
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqlServerTransferSchemaReader(scope.TargetConnectionString).ReadAsync(
                "dbo",
                "reader_rows",
                ["id", "tenant_id"],
                CancellationToken.None
            )
        );

        Assert.Contains("dbo.reader_rows", exception.Message);
        Assert.Contains("tenant_id", exception.Message);
        Assert.DoesNotContain("A write table requires a stable key", exception.Message);
    }
}
