using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlTransferSchemaReaderTests(PostgreSqlClosureFixture fixture)
    : IClassFixture<PostgreSqlClosureFixture>
{
    [Fact]
    public async Task ReadAsync_WhenTheNameIsAViewRatherThanATable_FailsNamingTheTableAndTheVisibilityRule()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE reader_rows (id integer NOT NULL CONSTRAINT pk_reader_rows PRIMARY KEY, name text NULL);"
                + " CREATE VIEW reader_view AS SELECT id, name FROM reader_rows;"
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgreSqlTransferSchemaReader(scope.Target).ReadAsync(
                scope.Schema,
                "reader_view",
                ["id"],
                CancellationToken.None
            )
        );

        Assert.Contains(scope.Schema + ".reader_view", exception.Message);
        Assert.Contains("view", exception.Message);
        Assert.Contains("partitioned table", exception.Message);
        Assert.DoesNotContain("Password", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_WhenAStableKeyColumnIsNotOnTheTable_FailsNamingTheTableAndTheColumn()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE reader_rows (id integer NOT NULL CONSTRAINT pk_reader_rows PRIMARY KEY, name text NULL);"
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgreSqlTransferSchemaReader(scope.Target).ReadAsync(
                scope.Schema,
                "reader_rows",
                ["id", "tenant_id"],
                CancellationToken.None
            )
        );

        Assert.Contains(scope.Schema + ".reader_rows", exception.Message);
        Assert.Contains("tenant_id", exception.Message);
        Assert.DoesNotContain("A write table requires a stable key", exception.Message);
    }
}
