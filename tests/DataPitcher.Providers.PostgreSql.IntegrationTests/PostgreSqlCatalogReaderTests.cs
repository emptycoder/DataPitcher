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

        Assert.Equal(
            ["physical_second", "physical_first"],
            source.Table("declared_key").Definition.PrimaryKey!.Columns
        );
        Assert.Equal(typeof(int), source.Table("optional_orders").Column("customer_id").ClrType);
        Assert.True(source.Table("optional_orders").Column("customer_id").IsNullable);
        Assert.Null(source.Table("unique_only").Definition.PrimaryKey);
        Assert.Equal(["code"], Assert.Single(source.Table("unique_only").Definition.UniqueConstraints).Columns);
        Assert.Equal(["child_right", "child_left"], source.ForeignKey("fk_composite_child_parent").ChildColumns);
        Assert.Equal(["left_value", "right_value"], source.ForeignKey("fk_composite_child_parent").ParentColumns);
        Assert.False(target.ForeignKey("Target_FK_P_G").IsTrusted);
    }

    [Fact]
    public async Task ReadAsync_FollowsForeignKeysIntoOtherSchemas()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var other = scope.Schema + "_ref";
        await scope.ExecuteAsync($"CREATE SCHEMA \"{other}\"");
        try
        {
            await scope.ExecuteAsync(
                $"CREATE TABLE \"{other}\".statuses (code integer PRIMARY KEY, label text NOT NULL)"
            );
            await scope.ExecuteAsync(
                $"CREATE TABLE tickets (id integer PRIMARY KEY, status integer NOT NULL CONSTRAINT fk_tickets_status REFERENCES \"{other}\".statuses(code))"
            );
            var reader = new PostgreSqlCatalogReader(scope.Source);

            var catalog = await reader.ReadAsync(scope.Schema, CancellationToken.None);

            Assert.Contains(catalog.Tables, t => t.Definition.Schema == other && t.Definition.Name == "statuses");
            var foreignKey = catalog.ForeignKey("fk_tickets_status");
            Assert.Equal(scope.Schema, foreignKey.ChildTable.Schema);
            Assert.Equal(other, foreignKey.ParentTable.Schema);
        }
        finally
        {
            await scope.ExecuteAsync($"DROP SCHEMA \"{other}\" CASCADE");
        }
    }

    [Fact]
    public async Task ReadAsync_MapsCommonTypesAndNeverFailsOnExoticOnes()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE many_types (id bigint PRIMARY KEY, amount numeric NOT NULL, label varchar(40), key_id uuid NOT NULL, at timestamptz NOT NULL, tags integer[] NULL, spot point NULL, doc jsonb NULL)"
        );
        var reader = new PostgreSqlCatalogReader(scope.Source);

        var catalog = await reader.ReadAsync(scope.Schema, CancellationToken.None);
        var table = catalog.Tables.Single(t => t.Definition.Name == "many_types");
        var columns = table.Definition.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

        Assert.Equal(typeof(long), columns["id"].ClrType);
        Assert.Equal(typeof(decimal), columns["amount"].ClrType);
        Assert.Equal(typeof(string), columns["label"].ClrType);
        Assert.Equal(typeof(Guid), columns["key_id"].ClrType);
        Assert.Equal(typeof(DateTimeOffset), columns["at"].ClrType);
        Assert.Equal(typeof(object), columns["tags"].ClrType);
        Assert.Equal(typeof(object), columns["spot"].ClrType);
        Assert.Equal(typeof(string), columns["doc"].ClrType);
    }

    [Fact]
    public async Task ReadAsync_ReportsGeneratedAndBinaryColumns()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE preview_metadata (id integer PRIMARY KEY, payload bytea NOT NULL, calculated integer GENERATED ALWAYS AS (id + 1) STORED)"
        );

        var table = (
            await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None)
        ).Table("preview_metadata");

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
