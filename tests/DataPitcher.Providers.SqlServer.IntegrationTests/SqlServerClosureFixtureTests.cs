using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerClosureFixtureTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task Scope_WhenCreated_HasAllShapesAndEnabledUntrustedTargetForeignKey()
    {
        await using var scope = await fixture.CreateScopeAsync();
        Assert.Equal(
            16,
            await scope.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo');")
        );
        Assert.Equal(
            0,
            await scope.ScalarTargetAsync<int>(
                "SELECT is_disabled FROM sys.foreign_keys WHERE name = N'Target_FK_P_G';"
            )
        );
        Assert.Equal(
            1,
            await scope.ScalarTargetAsync<int>(
                "SELECT is_not_trusted FROM sys.foreign_keys WHERE name = N'Target_FK_P_G';"
            )
        );
    }
}
