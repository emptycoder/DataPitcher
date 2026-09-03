using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using DataPitcher.Providers.SqlServer;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerStagingTablesTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task Stages_UseNativeKeyTypesCompleteUniqueIndexAndFirstGeneration()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var schema = await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None);
        var table = schema.Table("declared_key").Definition;
        var keys = new Dictionary<TableDefinition, StableKeySelection> { { table, StableKeySelector.Select(table, null) } };
        await using var first = new SqlServerStagingTables(scope.SourceConnectionString, scope.TargetConnectionString, schema, keys);
        await using var second = new SqlServerStagingTables(scope.SourceConnectionString, scope.TargetConnectionString, schema, keys);
        var key = new StableKey([new("physical_second", 2), new("physical_first", 1)]);
        Assert.Single(await first.InsertSourceAsync(table, [key], 1, CancellationToken.None));
        Assert.Empty(await first.InsertSourceAsync(table, [key], 9, CancellationToken.None));
        await second.InsertSourceAsync(table, [key], 1, CancellationToken.None);
        Assert.NotEqual(first.SourceTableName(table), second.SourceTableName(table));
        Assert.Equal(1, await first.GenerationAsync(table, key, CancellationToken.None));
        Assert.Equal(2, await first.KeyColumnCountInUniqueIndexAsync(table, CancellationToken.None));
    }

    [Fact]
    public async Task Stages_WhenPlanIsSpecified_UseDeterministicNamesAndRemainAfterDisposal()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var schema = await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None);
        var table = schema.Table("declared_key").Definition;
        var keys = new Dictionary<TableDefinition, StableKeySelection> { { table, StableKeySelector.Select(table, null) } };
        var planId = Guid.NewGuid();
        var first = new SqlServerStagingTables(scope.SourceConnectionString, scope.TargetConnectionString, schema, keys, planId, false);
        var second = new SqlServerStagingTables(scope.SourceConnectionString, scope.TargetConnectionString, schema, keys, planId, false);
        var key = new StableKey([new("physical_second", 2), new("physical_first", 1)]);

        Assert.Equal(first.SourceTableName(table), second.SourceTableName(table));
        await first.InsertSourceAsync(table, [key], 1, CancellationToken.None);
        var sourceName = first.SourceTableName(table); var inputName = first.InputTableName(table);
        await first.DisposeAsync(); await second.DisposeAsync();

        Assert.Equal(1, await scope.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'" + SqlServerStagingTables.Qualified(sourceName) + "',N'U') IS NULL THEN 0 ELSE 1 END"));
        await scope.ExecuteAsync("DROP TABLE " + SqlServerStagingTables.Qualified(inputName) + "; DROP TABLE " + SqlServerStagingTables.Qualified(sourceName));
    }
}
