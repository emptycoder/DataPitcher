using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlContainerTests
{
    [Fact]
    public async Task PostgreSqlContainer_WhenStarted_AcceptsConnectionAndServesQuery()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();

        await container.StartAsync();

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version();";
        var result = await command.ExecuteScalarAsync();

        var version = Assert.IsType<string>(result);
        Assert.False(string.IsNullOrEmpty(version));
        Assert.Contains("PostgreSQL 17", version, StringComparison.Ordinal);
    }
}
