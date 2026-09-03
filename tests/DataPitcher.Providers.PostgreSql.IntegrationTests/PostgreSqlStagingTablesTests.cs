using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using DataPitcher.Providers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlStagingTablesTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlStagingTablesTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Stages_UseTypedKeysImmutableGenerationAndDifferentPhysicalNames()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var schema = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
        var table = schema.Table("declared_key").Definition;
        var selections = new Dictionary<TableDefinition, StableKeySelection>
        {
            [table] = StableKeySelector.Select(table, null),
        };
        await using var first = new PostgreSqlStagingTables(scope.Source, scope.Target, schema, selections);
        await using var second = new PostgreSqlStagingTables(scope.Source, scope.Target, schema, selections);
        var key = new StableKey([new("physical_second", 2), new("physical_first", 1)]);
        Assert.Single(await first.InsertSourceAsync(table, [key], 1, CancellationToken.None));
        Assert.Empty(await first.InsertSourceAsync(table, [key], 9, CancellationToken.None));
        await second.InsertSourceAsync(table, [key], 1, CancellationToken.None);
        Assert.NotEqual(first.SourceTableName(table), second.SourceTableName(table));
        Assert.Equal(1, await first.GenerationAsync(table, key, CancellationToken.None));
    }
}
