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
}
