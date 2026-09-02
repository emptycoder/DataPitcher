using DataPitcher.Core.Closure;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

// Slice 1 counted calls to the IClosureStore interface and proved the ALGORITHM batches its
// probes. That says nothing about the PROVIDER: PostgreSqlClosureStore.ProbeTargetAsync could
// still receive one batched call and internally issue one query per key. This test counts
// commands actually sent to PostgreSQL, via an Npgsql logger wired into the target
// NpgsqlDataSource, so it observes the driver's real database traffic rather than a method-call
// count at the store seam.
public sealed class PostgreSqlProbeBatchingTests : IClassFixture<PostgreSqlClosureFixture>
{
    private const int KeyCount = 40;

    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlProbeBatchingTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProbeTargetAsync_IssuesOneCommandPerFrontierTablePerGeneration_RegardlessOfKeyCount()
    {
        var recorder = new PostgreSqlCommandRecorder();
        await using var scope = await _fixture.CreateScopeAsync(recorder);
        await BothAsync(scope, "CREATE TABLE batch_parent (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE batch_child (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES batch_parent(id))");
        var parentValues = string.Join(", ", Enumerable.Range(1, KeyCount).Select(i => $"({i})"));
        var childValues = string.Join(", ", Enumerable.Range(1, KeyCount).Select(i => $"({i}, {i})"));
        await scope.ExecuteAsync($"INSERT INTO batch_parent VALUES {parentValues}; INSERT INTO batch_child VALUES {childValues};");
        var (source, target) = await ReadAsync(scope);
        var child = source.Table("batch_child").Definition;
        var parent = source.Table("batch_parent").Definition;
        var relationship = new ClosureRelationship(Fk(source, child, parent));
        var roots = Enumerable.Range(1, KeyCount).Select(i => Key("id", i)).ToArray();
        await using var store = new PostgreSqlClosureStore(scope.Source, scope.Target, source, target, Selections(child, parent));
        var result = await RunAsync(store, [relationship], Selections(child, parent), new ClosureRoot(child, roots, RootConflictPolicy.FailOnConflict));

        Assert.Equal(KeyCount, result.Rows.Count(row => row.Table == child));
        Assert.Equal(KeyCount, result.Rows.Count(row => row.Table == parent));
        Assert.Equal(1, recorder.Count("DataPitcher.ProbeTarget", child));
        Assert.Equal(1, recorder.Count("DataPitcher.ProbeTarget", parent));
        Assert.Equal(2, recorder.Count("DataPitcher.ProbeTarget"));
        Assert.False(recorder.AnyContainsLargeInList(threshold: 10));
    }

    private static async Task BothAsync(PostgreSqlClosureScope scope, string sql)
    {
        await scope.ExecuteAsync(sql);
        await scope.ExecuteTargetAsync(sql);
    }

    private static async Task<(PostgreSqlSchemaSnapshot Source, PostgreSqlSchemaSnapshot Target)> ReadAsync(PostgreSqlClosureScope scope)
    {
        var source = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
        var target = await new PostgreSqlCatalogReader(scope.Target).ReadAsync(scope.Schema, CancellationToken.None);
        return (source, target);
    }

    private static ForeignKeyDefinition Fk(PostgreSqlSchemaSnapshot catalog, TableDefinition child, TableDefinition parent) =>
        catalog.ForeignKeys.Single(x => x.ChildTable == child && x.ParentTable == parent);

    private static IReadOnlyDictionary<TableDefinition, StableKeySelection> Selections(params TableDefinition[] tables) =>
        tables.Distinct().ToDictionary(t => t, t => StableKeySelector.Select(t, null));

    private static StableKey Key(string column, object? value) => new([new KeyComponent(column, value)]);

    private static Task<ClosureResult> RunAsync(IClosureStore store, IReadOnlyCollection<ClosureRelationship> relationships, IReadOnlyDictionary<TableDefinition, StableKeySelection> selections, params ClosureRoot[] roots) =>
        new DependencyClosure(store).ComputeAsync(new ClosureRequest(roots, relationships, selections), CancellationToken.None);
}
