using DataPitcher.Providers.SqlServer;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerCatalogReaderTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task ReadAsync_UsesConstraintOrderAndReadsTrustInThreeCommands()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await using var wire = await SqlServerWireCommandRecorder.StartAsync(scope.SourceAdminConnectionString, "DataPitcher.Catalog");
        var source = await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None);
        var target = await new SqlServerCatalogReader(scope.TargetConnectionString).ReadAsync("dbo", CancellationToken.None);
        Assert.Equal(["physical_second", "physical_first"], source.Table("declared_key").Definition.PrimaryKey!.Columns);
        Assert.Equal(typeof(int), source.Table("optional_orders").Column("customer_id").ClrType);
        Assert.True(source.Table("optional_orders").Column("customer_id").IsNullable);
        Assert.Null(source.Table("unique_only").Definition.PrimaryKey);
        Assert.Equal(["code"], Assert.Single(source.Table("unique_only").Definition.UniqueConstraints).Columns);
        Assert.Equal(["child_right", "child_left"], source.ForeignKey("FK_composite_child_parent").ChildColumns);
        Assert.Equal(["left_value", "right_value"], source.ForeignKey("FK_composite_child_parent").ParentColumns);
        Assert.True(target.ForeignKey("Target_FK_P_G").IsEnforced);
        Assert.False(target.ForeignKey("Target_FK_P_G").IsTrusted);
        Assert.Equal(3, await wire.Count("DataPitcher.Catalog"));
    }

    [Fact]
    public async Task ReadAsync_WhenColumnTypeIsUnmapped_ThrowsNotSupportedException()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("CREATE TABLE dbo.unmapped_type (id int NOT NULL PRIMARY KEY, amount decimal(18,2) NOT NULL)");
        var reader = new SqlServerCatalogReader(scope.SourceConnectionString);

        await Assert.ThrowsAsync<NotSupportedException>(() => reader.ReadAsync("dbo", CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_UsesThreeTaggedMetadataCommandsRegardlessOfAddedTableCount()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await using var wire = await SqlServerWireCommandRecorder.StartAsync(scope.SourceAdminConnectionString, "DataPitcher.Catalog");

        await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None);
        var initialCommandCount = await wire.Count("DataPitcher.Catalog");
        for (var index = 0; index < 50; index++)
            await scope.ExecuteAsync($"CREATE TABLE dbo.added_{index} (id int NOT NULL PRIMARY KEY)");
        await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None);
        var commands = await wire.SqlTextsAsync();

        Assert.Equal(3, initialCommandCount);
        Assert.Equal(3, await wire.Count("DataPitcher.Catalog") - initialCommandCount);
        Assert.DoesNotContain(commands, command => command.Contains("COUNT(*)", StringComparison.Ordinal));
    }
}
