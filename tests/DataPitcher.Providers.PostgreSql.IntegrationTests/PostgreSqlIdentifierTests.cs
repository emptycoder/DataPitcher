using DataPitcher.Providers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlIdentifierTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlIdentifierTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Quote_EscapesEmbeddedQuotesAndExecutesCaseSensitiveReservedNames()
    {
        const string table = "Select\"Rows";
        Assert.Equal("\"Select\"\"Rows\"", PostgreSqlIdentifier.Quote(table));

        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            $"CREATE TABLE {PostgreSqlIdentifier.Qualified(scope.Schema, table)} (\"Value\" integer PRIMARY KEY);"
        );
        await scope.ExecuteAsync(
            $"INSERT INTO {PostgreSqlIdentifier.Qualified(scope.Schema, table)} (\"Value\") VALUES (1);"
        );

        Assert.Equal(
            1L,
            await scope.ScalarAsync<long>(
                $"SELECT count(*) FROM {PostgreSqlIdentifier.Qualified(scope.Schema, table)};"
            )
        );
    }
}
