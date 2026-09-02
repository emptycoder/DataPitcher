using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using DataPitcher.Providers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlTransferModelsTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlTransferModelsTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ReadAsync_MapsEveryWritableColumnToAnExplicitProviderType()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text COLLATE \"C\" NOT NULL, stamp bigint NOT NULL, computed integer GENERATED ALWAYS AS (id + 1) STORED);");
        var table = await new PostgreSqlTransferSchemaReader(scope.Target).ReadAsync(scope.Schema, "transfer_rows", ["id"], CancellationToken.None);
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Integer, table.Column("id").ProviderType);
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Text, table.Column("code").ProviderType);
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Bigint, table.Column("stamp").ProviderType);
        Assert.True(table.Column("computed").IsGenerated);
        Assert.Equal("C", table.Column("code").Collation);
    }

    [Fact]
    public void WriteTable_ExcludesProtectedColumnsAndRoundTripsNativeStableKeys()
    {
        var table = PostgreSqlTransferTestData.Table("dp");
        Assert.Equal(["id", "code"], table.InsertColumns.Select(column => column.Name));
        Assert.Equal("code", Assert.Single(table.UpdateColumns).Name);
        var key = new StableKey([new KeyComponent("id", 7)]);
        Assert.Equal(key, PostgreSqlStableKeyCodec.Decode(PostgreSqlStableKeyCodec.Encode(key, table), table));
    }
}
