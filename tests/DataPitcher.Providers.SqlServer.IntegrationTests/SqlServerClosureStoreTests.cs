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
            (await store.ExpandAsync(fk, [new StableKey([new("id", 1)])], CancellationToken.None)).Keys,
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

    [Fact]
    public async Task ProbeTargetAsync_WhenAnUnrelatedTargetRowHoldsTheSameKeyValue_ReportsTheKeyValuePresent()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateProbeTablesAsync(scope, "int");
        await scope.ExecuteAsync("INSERT dbo.probe_rows VALUES (42, N'source record');");
        await scope.ExecuteTargetAsync("INSERT dbo.probe_rows VALUES (42, N'unrelated target record');");

        var probe = await ProbeAsync(scope, new StableKey([new("id", 42)]));

        Assert.True(probe.Exists);
        Assert.Equal(new StableKey([new("id", 42)]), probe.TargetKey);
    }

    [Fact]
    public async Task ProbeTargetAsync_WhenTheTargetKeyDiffersOnlyByCaseUnderACaseSensitiveColumn_ReportsAbsent()
    {
        await using var scope = await fixture.CreateScopeAsync();
        // The target column is case-sensitive while the database default is not: 'acme' and 'ACME' are two rows there.
        await CreateProbeTablesAsync(scope, "nvarchar(32) COLLATE Latin1_General_CS_AS");
        await scope.ExecuteAsync("INSERT dbo.probe_rows VALUES (N'ACME', N'source record');");
        await scope.ExecuteTargetAsync("INSERT dbo.probe_rows VALUES (N'acme', N'target record');");

        var probe = await ProbeAsync(scope, new StableKey([new("id", "ACME")]));

        Assert.False(probe.Exists);
    }

    [Fact]
    public async Task ProbeTargetAsync_WhenTheTargetKeyDiffersOnlyByCaseUnderACaseInsensitiveColumn_ReportsPresent()
    {
        await using var scope = await fixture.CreateScopeAsync();
        // Under the target's case-insensitive key (the database default) 'acme' and 'ACME' are one row: inserting
        // 'ACME' would violate that key.
        await CreateProbeTablesAsync(scope, "nvarchar(32)");
        await scope.ExecuteAsync("INSERT dbo.probe_rows VALUES (N'ACME', N'source record');");
        await scope.ExecuteTargetAsync("INSERT dbo.probe_rows VALUES (N'acme', N'target record');");

        var probe = await ProbeAsync(scope, new StableKey([new("id", "ACME")]));

        Assert.True(probe.Exists);
        Assert.Equal(new StableKey([new("id", "acme")]), probe.TargetKey);
    }

    [Fact]
    public async Task ProbeTargetAsync_WhenNoTargetRowHoldsTheKeyValue_ReportsAbsent()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateProbeTablesAsync(scope, "int");
        await scope.ExecuteAsync("INSERT dbo.probe_rows VALUES (42, N'source record');");

        var probe = await ProbeAsync(scope, new StableKey([new("id", 42)]));

        Assert.False(probe.Exists);
        Assert.Null(probe.TargetKey);
    }

    private static async Task CreateProbeTablesAsync(SqlServerClosureScope scope, string keyType)
    {
        var ddl =
            $"CREATE TABLE dbo.probe_rows (id {keyType} NOT NULL CONSTRAINT PK_probe_rows PRIMARY KEY, name nvarchar(64) NOT NULL);";
        await scope.ExecuteAsync(ddl);
        await scope.ExecuteTargetAsync(ddl);
    }

    private static async Task<TargetProbe> ProbeAsync(SqlServerClosureScope scope, StableKey key)
    {
        var source = await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync(
            "dbo",
            CancellationToken.None
        );
        var target = await new SqlServerCatalogReader(scope.TargetConnectionString).ReadAsync(
            "dbo",
            CancellationToken.None
        );
        var table = source.Table("probe_rows").Definition;
        await using var store = new SqlServerClosureStore(
            scope.SourceConnectionString,
            scope.TargetConnectionString,
            source,
            target,
            new Dictionary<TableDefinition, StableKeySelection> { [table] = StableKeySelector.Select(table, null) }
        );
        var probes = await store.ProbeTargetAsync(table, [], [key], CancellationToken.None);
        return probes[key];
    }
}
