using DataPitcher.Core.Closure;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using DataPitcher.Providers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlClosureStoreTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlClosureStoreTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ExpandAsync_UsesForeignKeyPositionRatherThanPhysicalColumnOrder()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "INSERT INTO composite_parent VALUES (7,8); INSERT INTO composite_child VALUES (1,8,7);"
        );
        var catalog = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
        var child = catalog.Table("composite_child").Definition;
        var parent = catalog.Table("composite_parent").Definition;
        var relationship = new ClosureRelationship(catalog.ForeignKey("fk_composite_child_parent"));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            catalog,
            catalog,
            new Dictionary<TableDefinition, StableKeySelection>
            {
                [child] = StableKeySelector.Select(child, null),
                [parent] = StableKeySelector.Select(parent, null),
            }
        );
        var result = await store.ExpandAsync(relationship, [new StableKey([new("id", 1)])], CancellationToken.None);
        Assert.Contains(result.Keys, key => key == new StableKey([new("left_value", 7), new("right_value", 8)]));
    }

    [Fact]
    public async Task ExpandAsync_WhenRelationshipIsInbound_ReversesTheJoinDirection()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync("INSERT INTO customers VALUES (1,'C1'); INSERT INTO orders VALUES (10,1);");
        var catalog = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
        var customers = catalog.Table("customers").Definition;
        var orders = catalog.Table("orders").Definition;
        var foreignKey = catalog.ForeignKeys.Single(fk => fk.ChildTable == orders && fk.ParentTable == customers);
        var relationship = new ClosureRelationship(foreignKey, isInbound: true);
        var selections = new Dictionary<TableDefinition, StableKeySelection>
        {
            [customers] = StableKeySelector.Select(customers, null),
            [orders] = StableKeySelector.Select(orders, null),
        };
        await using var store = new PostgreSqlClosureStore(scope.Source, scope.Target, catalog, catalog, selections);
        var result = await store.ExpandAsync(
            relationship,
            [new StableKey([new("customer_id", 1)])],
            CancellationToken.None
        );
        Assert.Contains(result.Keys, key => key == new StableKey([new("order_id", 10)]));
    }

    [Fact]
    public async Task SeedRootKeysAsync_And_InsertNewKeysAsync_PreserveEarlierGenerationOnRediscovery()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var source = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
        var customers = source.Table("customers").Definition;
        var selections = new Dictionary<TableDefinition, StableKeySelection>
        {
            [customers] = StableKeySelector.Select(customers, null),
        };
        await using var store = new PostgreSqlClosureStore(scope.Source, scope.Target, source, source, selections);
        var key = new StableKey([new("customer_id", 1)]);
        Assert.Single(await store.SeedRootKeysAsync(customers, [key], CancellationToken.None));
        Assert.Empty(await store.InsertNewKeysAsync(customers, [key], 5, CancellationToken.None));
    }

    [Fact]
    public async Task ProbeTargetAsync_BatchesExistenceAndReportsTargetConstraintState()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var source = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
        var target = await new PostgreSqlCatalogReader(scope.Target).ReadAsync(scope.Schema, CancellationToken.None);
        var parents = source.Table("untrusted_parents").Definition;
        var grandparents = source.Table("untrusted_grandparents").Definition;
        var relationship = new ClosureRelationship(source.ForeignKey("FK_P_G"));
        var manual = ClosureRelationship.Manual(
            "manual-relationship",
            parents,
            grandparents,
            ["grandparent_id"],
            ["id"]
        );
        var selections = new Dictionary<TableDefinition, StableKeySelection>
        {
            [parents] = StableKeySelector.Select(parents, null),
            [grandparents] = StableKeySelector.Select(grandparents, null),
        };
        await using var store = new PostgreSqlClosureStore(scope.Source, scope.Target, source, target, selections);
        var present = new StableKey([new("id", 2)]);
        var absent = new StableKey([new("id", 999)]);
        var probes = await store.ProbeTargetAsync(
            parents,
            [relationship, manual],
            [present, absent],
            CancellationToken.None
        );
        Assert.True(probes[present].Exists);
        Assert.False(probes[absent].Exists);
        var state = probes[present].Constraints[relationship];
        Assert.True(state.IsPresent);
        Assert.True(state.IsEnforced);
        Assert.False(state.IsTrusted);
        Assert.Equal("Target_FK_P_G", state.ConstraintName);
        Assert.False(probes[present].Constraints[manual].IsPresent);
    }

    [Fact]
    public async Task ProbeTargetAsync_WhenNoMatchingTargetForeignKeyExists_ReportsAbsentConstraint()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var source = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
        var target = await new PostgreSqlCatalogReader(scope.Target).ReadAsync(scope.Schema, CancellationToken.None);
        var strippedTarget = new PostgreSqlSchemaSnapshot(target.Tables, []);
        var orders = source.Table("orders").Definition;
        var customers = source.Table("customers").Definition;
        var foreignKey = source.ForeignKeys.Single(fk => fk.ChildTable == orders && fk.ParentTable == customers);
        var relationship = new ClosureRelationship(foreignKey);
        var selections = new Dictionary<TableDefinition, StableKeySelection>
        {
            [orders] = StableKeySelector.Select(orders, null),
            [customers] = StableKeySelector.Select(customers, null),
        };
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            strippedTarget,
            selections
        );
        var key = new StableKey([new("order_id", 1)]);
        var probes = await store.ProbeTargetAsync(orders, [relationship], [key], CancellationToken.None);
        Assert.False(probes[key].Constraints[relationship].IsPresent);
    }
}
