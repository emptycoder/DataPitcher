using DataPitcher.Core.Closure;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using DataPitcher.Providers.SqlServer;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerClosureStoreTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task ExpandAsync_UsesForeignKeyPositionAndProbeReportsTargetTrust()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "INSERT dbo.composite_parent VALUES (7,8); INSERT dbo.composite_child VALUES (1,8,7);"
        );
        var source = await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync(
            "dbo",
            CancellationToken.None
        );
        var target = await new SqlServerCatalogReader(scope.TargetConnectionString).ReadAsync(
            "dbo",
            CancellationToken.None
        );
        var child = source.Table("composite_child").Definition;
        var parent = source.Table("composite_parent").Definition;
        var p = source.Table("untrusted_parents").Definition;
        var fk = new ClosureRelationship(source.ForeignKey("FK_composite_child_parent"));
        var keys = new Dictionary<TableDefinition, StableKeySelection>
        {
            [child] = StableKeySelector.Select(child, null),
            [parent] = StableKeySelector.Select(parent, null),
            [p] = StableKeySelector.Select(p, null),
        };
        await using var store = new SqlServerClosureStore(
            scope.SourceConnectionString,
            scope.TargetConnectionString,
            source,
            target,
            keys
        );
        Assert.Contains(
            await store.ExpandAsync(fk, [new StableKey([new("id", 1)])], CancellationToken.None),
            key => key == new StableKey([new("left_value", 7), new("right_value", 8)])
        );
        var g = source.Table("untrusted_grandparents").Definition;
        var probe = await store.ProbeTargetAsync(
            p,
            [new ClosureRelationship(source.ForeignKey("FK_P_G"))],
            [new StableKey([new("id", 2)])],
            CancellationToken.None
        );
        Assert.True(probe[new StableKey([new("id", 2)])].Exists);
        Assert.False(probe.Values.Single().Constraints.Values.Single().IsTrusted);
    }
}
