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
        await scope.ExecuteTargetAsync(
            "CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text COLLATE \"C\" NOT NULL, stamp bigint NOT NULL, computed integer GENERATED ALWAYS AS (id + 1) STORED);"
        );
        var table = await new PostgreSqlTransferSchemaReader(scope.Target).ReadAsync(
            scope.Schema,
            "transfer_rows",
            ["id"],
            CancellationToken.None
        );
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

    [Fact]
    public void WriteTable_ctor_WhenNoColumnIsMarkedStableKey_ThrowsArgumentException()
    {
        var columns = new[]
        {
            new PostgreSqlWriteColumn("code", "text", NpgsqlTypes.NpgsqlDbType.Text, false, false, false, false, "C"),
        };
        Assert.Throws<ArgumentException>(() =>
            new PostgreSqlWriteTable(new TableAddress("dp", "no_key_rows"), columns)
        );
    }

    [Fact]
    public void TransferRow_ExposesTheStableKeyItWasConstructedWith()
    {
        var key = new StableKey([new KeyComponent("id", 9)]);
        var row = new PostgreSqlTransferRow(key, new Dictionary<string, object?> { ["id"] = 9 });
        Assert.Equal(key, row.StableKey);
    }

    [Fact]
    public void StableKeyCodec_RoundTripsACompositeBigintAndTextStableKey()
    {
        var table = new PostgreSqlWriteTable(
            new TableAddress("dp", "composite_stable_rows"),
            [
                new("stamp", "bigint", NpgsqlTypes.NpgsqlDbType.Bigint, true, false, false, false, null),
                new("region", "text", NpgsqlTypes.NpgsqlDbType.Text, true, false, false, false, "C"),
            ]
        );
        var key = new StableKey([new KeyComponent("stamp", 4_000_000_000L), new KeyComponent("region", "east")]);
        Assert.Equal(key, PostgreSqlStableKeyCodec.Decode(PostgreSqlStableKeyCodec.Encode(key, table), table));
    }

    [Fact]
    public void StableKeyCodec_Encode_WhenAComponentValueIsNull_ThrowsArgumentException()
    {
        var table = PostgreSqlTransferTestData.Table("dp");
        var key = new StableKey([new KeyComponent("id", null)]);
        Assert.Throws<ArgumentException>(() => PostgreSqlStableKeyCodec.Encode(key, table));
    }

    [Fact]
    public void StableKeyCodec_Encode_WhenTheValuesClrTypeDoesNotMatchTheDeclaredProviderType_ThrowsNotSupportedException()
    {
        var table = PostgreSqlTransferTestData.Table("dp");
        var key = new StableKey([new KeyComponent("id", 7L)]);
        Assert.Throws<NotSupportedException>(() => PostgreSqlStableKeyCodec.Encode(key, table));
    }

    [Fact]
    public void StableKeyCodec_Encode_WhenTheProviderTypeIsNotIntegerBigintOrText_ThrowsNotSupportedException()
    {
        var table = new PostgreSqlWriteTable(
            new TableAddress("dp", "uuid_key_rows"),
            [new("id", "uuid", NpgsqlTypes.NpgsqlDbType.Uuid, true, false, false, false, null)]
        );
        var key = new StableKey([new KeyComponent("id", Guid.NewGuid())]);
        Assert.Throws<NotSupportedException>(() => PostgreSqlStableKeyCodec.Encode(key, table));
    }

    [Fact]
    public void StableKeyCodec_Decode_WhenTheProviderTypeIsNotIntegerBigintOrText_ThrowsNotSupportedException()
    {
        var table = new PostgreSqlWriteTable(
            new TableAddress("dp", "uuid_key_rows"),
            [new("id", "uuid", NpgsqlTypes.NpgsqlDbType.Uuid, true, false, false, false, null)]
        );
        Assert.Throws<NotSupportedException>(() => PostgreSqlStableKeyCodec.Decode([], table));
    }

    [Fact]
    public void StableKeyCodec_Decode_WhenBytesHaveTrailingData_ThrowsArgumentException()
    {
        var table = PostgreSqlTransferTestData.Table("dp");
        var key = new StableKey([new KeyComponent("id", 7)]);
        var encoded = PostgreSqlStableKeyCodec.Encode(key, table);
        var withTrailingByte = encoded.Concat(new byte[] { 0 }).ToArray();
        Assert.Throws<ArgumentException>(() => PostgreSqlStableKeyCodec.Decode(withTrailingByte, table));
    }

    [Fact]
    public async Task ReadAsync_MapsUuidColumns()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id uuid PRIMARY KEY);");
        var table = await new PostgreSqlTransferSchemaReader(scope.Target).ReadAsync(
            scope.Schema,
            "transfer_rows",
            ["id"],
            CancellationToken.None
        );
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Uuid, table.Column("id").ProviderType);
    }

    [Fact]
    public async Task ReadAsync_WhenAColumnTypeIsNotSupported_ThrowsNotSupportedException()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, spot point NOT NULL);");
        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            new PostgreSqlTransferSchemaReader(scope.Target).ReadAsync(
                scope.Schema,
                "transfer_rows",
                ["id"],
                CancellationToken.None
            )
        );
        Assert.Contains("point", error.Message, StringComparison.Ordinal);
    }
}
