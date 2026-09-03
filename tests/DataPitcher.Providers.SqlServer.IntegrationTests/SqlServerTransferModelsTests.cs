using System.Data;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using DataPitcher.Providers.SqlServer;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerTransferModelsTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task ReadAsync_MapsWritableColumnsAndProtectsTransferColumns()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.transfer_rows (id bigint IDENTITY PRIMARY KEY, code nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL, stamp rowversion, computed AS LEN(code));"
        );
        var table = await new SqlServerTransferSchemaReader(scope.TargetConnectionString).ReadAsync(
            "dbo",
            "transfer_rows",
            ["id"],
            CancellationToken.None
        );
        Assert.Equal(SqlDbType.BigInt, table.Column("id").ProviderType);
        Assert.True(table.Column("id").IsIdentity);
        Assert.True(table.Column("stamp").IsRowVersion);
        Assert.True(table.Column("computed").IsComputed);
        Assert.Single(table.UpdateColumns, column => column.Name == "code");
        Assert.Equal("Latin1_General_100_BIN2", table.Column("code").Collation);
    }

    [Fact]
    public void StableKeys_RoundTripAndRequireAKey()
    {
        var table = new SqlServerWriteTable(
            new TableAddress("dbo", "rows"),
            [new("id", "int", typeof(int), SqlDbType.Int, true, false, false, false, false, null)]
        );
        var key = new StableKey([new KeyComponent("id", 7)]);
        Assert.Equal(key, SqlServerStableKeyCodec.Decode(SqlServerStableKeyCodec.Encode(key, table), table));
        Assert.Throws<ArgumentException>(() =>
            new SqlServerWriteTable(
                new TableAddress("dbo", "no_key"),
                [
                    new(
                        "code",
                        "nvarchar(64)",
                        typeof(string),
                        SqlDbType.NVarChar,
                        false,
                        false,
                        false,
                        false,
                        false,
                        null
                    ),
                ]
            )
        );
    }

    [Fact]
    public void WriteTable_ExcludesComputedAndRowVersionColumnsFromInsertColumns()
    {
        var table = new SqlServerWriteTable(
            new TableAddress("dbo", "rows"),
            [
                new("id", "int", typeof(int), SqlDbType.Int, true, false, false, false, false, null),
                new(
                    "code",
                    "nvarchar(64)",
                    typeof(string),
                    SqlDbType.NVarChar,
                    false,
                    false,
                    false,
                    false,
                    false,
                    null
                ),
                new("computed", "int", typeof(int), SqlDbType.Int, false, false, true, false, false, null),
                new("stamp", "varbinary(8)", typeof(byte[]), SqlDbType.Variant, false, false, false, true, false, null),
            ]
        );
        Assert.Equal(["id", "code"], table.InsertColumns.Select(column => column.Name));
        Assert.Equal("code", Assert.Single(table.UpdateColumns).Name);
    }

    [Fact]
    public void TransferRow_ExposesTheStableKeyItWasConstructedWith()
    {
        var key = new StableKey([new KeyComponent("id", 9)]);
        var row = new SqlServerTransferRow(key, new Dictionary<string, object?> { ["id"] = 9 });
        Assert.Equal(key, row.StableKey);
    }

    [Fact]
    public void StableKeyCodec_RoundTripsACompositeBigintAndTextStableKey()
    {
        var table = new SqlServerWriteTable(
            new TableAddress("dbo", "composite_stable_rows"),
            [
                new("stamp", "bigint", typeof(long), SqlDbType.BigInt, true, false, false, false, false, null),
                new(
                    "region",
                    "nvarchar(64)",
                    typeof(string),
                    SqlDbType.NVarChar,
                    true,
                    false,
                    false,
                    false,
                    false,
                    null
                ),
            ]
        );
        var key = new StableKey([new KeyComponent("stamp", 4_000_000_000L), new KeyComponent("region", "east")]);
        Assert.Equal(key, SqlServerStableKeyCodec.Decode(SqlServerStableKeyCodec.Encode(key, table), table));
    }

    [Fact]
    public void StableKeyCodec_RoundTripsNvarcharMax()
    {
        var table = new SqlServerWriteTable(
            new TableAddress("dbo", "text_key_rows"),
            [new("code", "nvarchar(max)", typeof(string), SqlDbType.NVarChar, true, false, false, false, false, null)]
        );
        var key = new StableKey([new KeyComponent("code", "some long value")]);
        Assert.Equal(key, SqlServerStableKeyCodec.Decode(SqlServerStableKeyCodec.Encode(key, table), table));
    }

    [Fact]
    public void StableKeyCodec_Encode_WhenAComponentValueIsNull_ThrowsArgumentException()
    {
        var table = SqlServerTransferTestData.Table();
        var key = new StableKey([new KeyComponent("id", null)]);
        Assert.Throws<ArgumentException>(() => SqlServerStableKeyCodec.Encode(key, table));
    }

    [Fact]
    public void StableKeyCodec_Encode_WhenTheValuesClrTypeDoesNotMatchTheDeclaredProviderType_ThrowsNotSupportedException()
    {
        var table = SqlServerTransferTestData.Table();
        var key = new StableKey([new KeyComponent("id", 7L)]);
        Assert.Throws<NotSupportedException>(() => SqlServerStableKeyCodec.Encode(key, table));
    }

    [Fact]
    public void StableKeyCodec_Encode_WhenABigIntComponentIsNotALong_RejectsTheInvalidKey()
    {
        var table = new SqlServerWriteTable(
            new TableAddress("dbo", "rows"),
            [new("id", "bigint", typeof(long), SqlDbType.BigInt, true, false, false, false, false, null)]
        );
        var key = new StableKey([new KeyComponent("id", 7)]);
        Assert.Throws<NotSupportedException>(() => SqlServerStableKeyCodec.Encode(key, table));
    }

    [Fact]
    public void StableKeyCodec_Encode_WhenTheProviderTypeIsUnsupported_ThrowsNotSupportedException()
    {
        var table = new SqlServerWriteTable(
            new TableAddress("dbo", "uid_key_rows"),
            [
                new(
                    "id",
                    "uniqueidentifier",
                    typeof(Guid),
                    SqlDbType.UniqueIdentifier,
                    true,
                    false,
                    false,
                    false,
                    false,
                    null
                ),
            ]
        );
        var key = new StableKey([new KeyComponent("id", Guid.NewGuid())]);
        Assert.Throws<NotSupportedException>(() => SqlServerStableKeyCodec.Encode(key, table));
    }

    [Fact]
    public void StableKeyCodec_Decode_WhenTheProviderTypeIsUnsupported_ThrowsNotSupportedException()
    {
        var table = new SqlServerWriteTable(
            new TableAddress("dbo", "uid_key_rows"),
            [
                new(
                    "id",
                    "uniqueidentifier",
                    typeof(Guid),
                    SqlDbType.UniqueIdentifier,
                    true,
                    false,
                    false,
                    false,
                    false,
                    null
                ),
            ]
        );
        Assert.Throws<NotSupportedException>(() => SqlServerStableKeyCodec.Decode([], table));
    }

    [Fact]
    public void StableKeyCodec_Decode_WhenBytesHaveTrailingData_ThrowsArgumentException()
    {
        var table = SqlServerTransferTestData.Table();
        var key = new StableKey([new KeyComponent("id", 7)]);
        var encoded = SqlServerStableKeyCodec.Encode(key, table);
        var withTrailingByte = encoded.Concat(new byte[] { 0 }).ToArray();
        Assert.Throws<ArgumentException>(() => SqlServerStableKeyCodec.Decode(withTrailingByte, table));
    }

    [Fact]
    public async Task ReadAsync_WhenAColumnTypeIsNotSupported_ThrowsNotSupportedException()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int PRIMARY KEY, flag bit NOT NULL);");
        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            new SqlServerTransferSchemaReader(scope.TargetConnectionString).ReadAsync(
                "dbo",
                "transfer_rows",
                ["id"],
                CancellationToken.None
            )
        );
        Assert.Contains("bit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_WhenAColumnIsNvarcharMax_PreservesTheUnboundedStoreType()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.transfer_rows (id int PRIMARY KEY, note nvarchar(max) NOT NULL);"
        );
        var table = await new SqlServerTransferSchemaReader(scope.TargetConnectionString).ReadAsync(
            "dbo",
            "transfer_rows",
            ["id"],
            CancellationToken.None
        );
        Assert.Equal("nvarchar(max)", table.Column("note").StoreType);
    }
}
