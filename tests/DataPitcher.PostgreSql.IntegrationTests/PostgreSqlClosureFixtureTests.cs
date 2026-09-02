using Xunit;

namespace DataPitcher.PostgreSql.IntegrationTests;

public sealed class PostgreSqlClosureFixtureTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlClosureFixtureTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Scope_WhenCreated_ContainsDeterministicSchemaAndUnvalidatedForeignKey()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        Assert.Equal(16, await scope.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema();"));
        Assert.False(await scope.ScalarTargetAsync<bool>("SELECT convalidated FROM pg_constraint WHERE conname = 'Target_FK_P_G';"));
    }
}
