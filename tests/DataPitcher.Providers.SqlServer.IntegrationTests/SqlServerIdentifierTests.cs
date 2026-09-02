using DataPitcher.Providers.SqlServer;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerIdentifierTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task Quote_ExecutesAnIdentifierContainingTheClosingBracketInjectionCharacter()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string table = "Select]Rows";
        var qualified = SqlServerIdentifier.Qualified("dbo", table);
        await scope.ExecuteAsync($"CREATE TABLE {qualified} ([Value] int NOT NULL PRIMARY KEY); INSERT {qualified} ([Value]) VALUES (1);");
        Assert.Equal(1, await scope.ScalarAsync<int>($"SELECT COUNT(*) FROM {qualified};"));
    }
}
