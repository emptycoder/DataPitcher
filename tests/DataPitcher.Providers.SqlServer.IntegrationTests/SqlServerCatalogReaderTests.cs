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
        await using var wire = await SqlServerWireCommandRecorder.StartAsync(
            scope.SourceAdminConnectionString,
            "DataPitcher.Catalog"
        );
        var source = await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync(
            "dbo",
            CancellationToken.None
        );
        var target = await new SqlServerCatalogReader(scope.TargetConnectionString).ReadAsync(
            "dbo",
            CancellationToken.None
        );
        Assert.Equal(
            ["physical_second", "physical_first"],
            source.Table("declared_key").Definition.PrimaryKey!.Columns
        );
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
    public async Task ReadAsync_FollowsForeignKeysIntoOtherSchemas()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync("CREATE SCHEMA ref;");
        await scope.ExecuteAsync(
            "CREATE TABLE ref.statuses (code int NOT NULL PRIMARY KEY, label nvarchar(40) NOT NULL)"
        );
        await scope.ExecuteAsync(
            "CREATE TABLE dbo.tickets (id int NOT NULL PRIMARY KEY, status int NOT NULL CONSTRAINT FK_tickets_status REFERENCES ref.statuses(code))"
        );
        var reader = new SqlServerCatalogReader(scope.SourceConnectionString);

        var catalog = await reader.ReadAsync("dbo", CancellationToken.None);

        Assert.Contains(catalog.Tables, t => t.Definition.Schema == "ref" && t.Definition.Name == "statuses");
        var foreignKey = catalog.ForeignKey("FK_tickets_status");
        Assert.Equal("dbo", foreignKey.ChildTable.Schema);
        Assert.Equal("ref", foreignKey.ParentTable.Schema);
        Assert.Same(catalog.Table("ref", "statuses").Definition, foreignKey.ParentTable);
    }

    [Fact]
    public async Task ReadAsync_MapsCommonTypesAndNeverFailsOnExoticOnes()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE dbo.many_types (id bigint NOT NULL PRIMARY KEY, amount decimal(18,2) NOT NULL, label varchar(40) NULL, note nvarchar(max) NULL, at datetime2(3) NOT NULL, key_id uniqueidentifier NOT NULL, shape geography NULL, path hierarchyid NULL, anything sql_variant NULL, stamp rowversion)"
        );
        var reader = new SqlServerCatalogReader(scope.SourceConnectionString);

        var catalog = await reader.ReadAsync("dbo", CancellationToken.None);
        var table = catalog.Tables.Single(t => t.Definition.Name == "many_types");
        var columns = table.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

        Assert.Equal(typeof(long), columns["id"].ClrType);
        Assert.Equal(typeof(decimal), columns["amount"].ClrType);
        Assert.Equal("decimal(18,2)", columns["amount"].StoreType);
        Assert.Equal("varchar(40)", columns["label"].StoreType);
        Assert.Equal("nvarchar(max)", columns["note"].StoreType);
        Assert.Equal(typeof(DateTime), columns["at"].ClrType);
        Assert.Equal("datetime2(3)", columns["at"].StoreType);
        Assert.Equal(typeof(Guid), columns["key_id"].ClrType);
        Assert.Equal(typeof(object), columns["shape"].ClrType);
        Assert.Equal("geography", columns["shape"].StoreType);
        Assert.Equal(typeof(object), columns["path"].ClrType);
        Assert.Equal(typeof(object), columns["anything"].ClrType);
        Assert.Equal(typeof(byte[]), columns["stamp"].ClrType);
    }

    [Fact]
    public async Task ReadAsync_ReportsGeneratedAndBinaryColumns()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE dbo.preview_metadata (id int PRIMARY KEY, payload varbinary(max) NOT NULL, calculated AS id + 1)"
        );

        var table = (
            await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None)
        ).Table("preview_metadata");

        Assert.Equal(typeof(byte[]), table.Column("payload").ClrType);
        Assert.True(table.Column("calculated").IsGenerated);
    }

    [Fact]
    public async Task ReadAsync_UsesThreeTaggedMetadataCommandsRegardlessOfAddedTableCount()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await using var wire = await SqlServerWireCommandRecorder.StartAsync(
            scope.SourceAdminConnectionString,
            "DataPitcher.Catalog"
        );

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
