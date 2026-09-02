using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using DataPitcher.Providers.SqlServer;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerClosureStoreTypeMismatchTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task ProbeTargetAsync_WhenStableKeyComponentTypeDoesNotMatchColumn_ThrowsTheGuard()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("CREATE TABLE dbo.type_mismatch (id int NOT NULL PRIMARY KEY)");
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.type_mismatch (id int NOT NULL PRIMARY KEY)");
        var source = await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None);
        var target = await new SqlServerCatalogReader(scope.TargetConnectionString).ReadAsync("dbo", CancellationToken.None);
        var table = source.Table("type_mismatch").Definition;
        var keys = new Dictionary<TableDefinition, StableKeySelection> { [table] = StableKeySelector.Select(table, null) };
        await using var store = new SqlServerClosureStore(scope.SourceConnectionString, scope.TargetConnectionString, source, target, keys);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ProbeTargetAsync(table, [], [new StableKey([new("id", "wrong")])], CancellationToken.None));
        Assert.Equal("Stable-key CLR type does not match catalog metadata.", error.Message);
    }

    [Fact]
    public async Task ProbeTargetAsync_WhenStableKeyComponentIsNull_ThrowsTheGuard()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("CREATE TABLE dbo.null_key (id int NOT NULL PRIMARY KEY)");
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.null_key (id int NOT NULL PRIMARY KEY)");
        var source = await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None);
        var target = await new SqlServerCatalogReader(scope.TargetConnectionString).ReadAsync("dbo", CancellationToken.None);
        var table = source.Table("null_key").Definition;
        var keys = new Dictionary<TableDefinition, StableKeySelection> { [table] = StableKeySelector.Select(table, null) };
        await using var store = new SqlServerClosureStore(scope.SourceConnectionString, scope.TargetConnectionString, source, target, keys);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ProbeTargetAsync(table, [], [new StableKey([new("id", null)])], CancellationToken.None));
        Assert.Equal("Stable-key CLR type does not match catalog metadata.", error.Message);
    }
}
