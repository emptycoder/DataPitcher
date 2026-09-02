using DataPitcher.Providers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlCatalogReaderTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlCatalogReaderTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ReadAsync_UsesConstraintDeclarationOrderAndReadsValidation()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var source = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
        var target = await new PostgreSqlCatalogReader(scope.Target).ReadAsync(scope.Schema, CancellationToken.None);

        Assert.Equal(["physical_second", "physical_first"], source.Table("declared_key").Definition.PrimaryKey!.Columns);
        Assert.Equal(typeof(int), source.Table("optional_orders").Column("customer_id").ClrType);
        Assert.True(source.Table("optional_orders").Column("customer_id").IsNullable);
        Assert.Null(source.Table("unique_only").Definition.PrimaryKey);
        Assert.Equal(["code"], Assert.Single(source.Table("unique_only").Definition.UniqueConstraints).Columns);
        Assert.Equal(["child_right", "child_left"], source.ForeignKey("fk_composite_child_parent").ChildColumns);
        Assert.Equal(["left_value", "right_value"], source.ForeignKey("fk_composite_child_parent").ParentColumns);
        Assert.False(target.ForeignKey("Target_FK_P_G").IsTrusted);
    }

    [Fact]
    public async Task ReadAsync_WhenColumnTypeIsUnmapped_ThrowsNotSupportedException()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync("CREATE TABLE unmapped_type (id integer PRIMARY KEY, amount numeric NOT NULL)");
        var reader = new PostgreSqlCatalogReader(scope.Source);

        await Assert.ThrowsAsync<NotSupportedException>(() => reader.ReadAsync(scope.Schema, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_ReportsGeneratedAndBinaryColumns()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync("CREATE TABLE preview_metadata (id integer PRIMARY KEY, payload bytea NOT NULL, calculated integer GENERATED ALWAYS AS (id + 1) STORED)");

        var table = (await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None)).Table("preview_metadata");

        Assert.Equal(typeof(byte[]), table.Column("payload").ClrType);
        Assert.True(table.Column("calculated").IsGenerated);
    }

    [Fact]
    public async Task ReadAsync_UsesThreeTaggedMetadataCommandsRegardlessOfAddedTableCount()
    {
        var recorder = new PostgreSqlCommandRecorder();
        await using var scope = await _fixture.CreateScopeAsync(recorder);

        await new PostgreSqlCatalogReader(scope.Target).ReadAsync(scope.Schema, CancellationToken.None);
        var initialCommandCount = recorder.Count("DataPitcher.Catalog");
        for (var index = 0; index < 50; index++)
            await scope.ExecuteTargetAsync($"CREATE TABLE added_{index} (id integer PRIMARY KEY)");
        await new PostgreSqlCatalogReader(scope.Target).ReadAsync(scope.Schema, CancellationToken.None);

        Assert.Equal(3, initialCommandCount);
        Assert.Equal(3, recorder.Count("DataPitcher.Catalog") - initialCommandCount);
        Assert.False(recorder.AnyContains("COUNT(*)"));
    }
}
