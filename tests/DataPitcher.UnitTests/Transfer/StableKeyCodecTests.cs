using System.Data;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using DataPitcher.Providers.PostgreSql;
using DataPitcher.Providers.SqlServer;
using NpgsqlTypes;
using Xunit;

namespace DataPitcher.UnitTests.Transfer;

public sealed class StableKeyCodecTests
{
    public static TheoryData<SqlDbType, object> SqlServerKeys =>
        new()
        {
            { SqlDbType.Int, 7 },
            { SqlDbType.BigInt, 4_000_000_000L },
            { SqlDbType.SmallInt, (short)-12 },
            { SqlDbType.TinyInt, (byte)200 },
            { SqlDbType.Bit, true },
            { SqlDbType.Char, "AB" },
            { SqlDbType.VarChar, "ACTIVE" },
            { SqlDbType.NChar, "Zürich" },
            { SqlDbType.NVarChar, "naïve ✓" },
            { SqlDbType.UniqueIdentifier, Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff") },
            { SqlDbType.Decimal, 1234567.8900m },
            { SqlDbType.Money, -19.99m },
            { SqlDbType.Date, new DateTime(2024, 2, 29) },
            { SqlDbType.DateTime, new DateTime(2024, 2, 29, 13, 45, 12, 340) },
            { SqlDbType.DateTime2, new DateTime(638_000_000_000_000_001L) },
            { SqlDbType.DateTimeOffset, new DateTimeOffset(2024, 2, 29, 13, 45, 12, TimeSpan.FromMinutes(-330)) },
            { SqlDbType.Time, new TimeSpan(0, 13, 45, 12, 7) },
            { SqlDbType.Binary, new byte[] { 0, 1, 2, 255 } },
            { SqlDbType.VarBinary, new byte[] { 9, 8, 7 } },
        };

    public static TheoryData<NpgsqlDbType, object> PostgreSqlKeys =>
        new()
        {
            { NpgsqlDbType.Integer, 7 },
            { NpgsqlDbType.Bigint, 4_000_000_000L },
            { NpgsqlDbType.Smallint, (short)-12 },
            { NpgsqlDbType.Boolean, false },
            { NpgsqlDbType.Numeric, 1234567.8900m },
            { NpgsqlDbType.Text, "naïve ✓" },
            { NpgsqlDbType.Varchar, "ACTIVE" },
            { NpgsqlDbType.Char, "AB" },
            { NpgsqlDbType.Name, "pg_catalog" },
            { NpgsqlDbType.Uuid, Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff") },
            { NpgsqlDbType.Bytea, new byte[] { 0, 1, 2, 255 } },
            { NpgsqlDbType.Date, new DateTime(2024, 2, 29) },
            { NpgsqlDbType.Date, new DateOnly(2024, 2, 29) },
            { NpgsqlDbType.Time, new TimeSpan(0, 13, 45, 12, 7) },
            { NpgsqlDbType.Time, new TimeOnly(13, 45, 12, 7) },
            { NpgsqlDbType.Timestamp, new DateTime(2024, 2, 29, 13, 45, 12, 340) },
            { NpgsqlDbType.TimestampTz, new DateTime(2024, 2, 29, 13, 45, 12, 340, DateTimeKind.Utc) },
            { NpgsqlDbType.TimestampTz, new DateTimeOffset(2024, 2, 29, 13, 45, 12, TimeSpan.FromHours(2)) },
            { NpgsqlDbType.TimeTz, new DateTimeOffset(1, 1, 2, 13, 45, 12, TimeSpan.FromHours(2)) },
            { NpgsqlDbType.Interval, TimeSpan.FromDays(3.5) },
        };

    [Theory]
    [MemberData(nameof(SqlServerKeys))]
    public void SqlServer_RoundTripsEverySupportedTypeToTheExactClrType(SqlDbType type, object value)
    {
        var table = SqlServerTable(("id", type));
        var key = new StableKey([new KeyComponent("id", value)]);

        var decoded = SqlServerStableKeyCodec.Decode(SqlServerStableKeyCodec.Encode(key, table), table);

        var component = Assert.Single(decoded.Components);
        Assert.IsType(value.GetType(), component.Value);
        Assert.Equal(value, component.Value);
        if (value is DateTime moment)
            Assert.Equal(moment.Kind, ((DateTime)component.Value!).Kind);
        Assert.True(SqlServerStableKeyCodec.Supports(type));
    }

    [Theory]
    [MemberData(nameof(PostgreSqlKeys))]
    public void PostgreSql_RoundTripsEverySupportedTypeToTheExactClrType(NpgsqlDbType type, object value)
    {
        var table = PostgreSqlTable(("id", type));
        var key = new StableKey([new KeyComponent("id", value)]);

        var decoded = PostgreSqlStableKeyCodec.Decode(PostgreSqlStableKeyCodec.Encode(key, table), table);

        var component = Assert.Single(decoded.Components);
        Assert.IsType(value.GetType(), component.Value);
        Assert.Equal(value, component.Value);
        if (value is DateTime moment)
            Assert.Equal(moment.Kind, ((DateTime)component.Value!).Kind);
        Assert.True(PostgreSqlStableKeyCodec.Supports(type));
    }

    [Fact]
    public void SqlServer_KeepsTheOriginalLayoutForIntBigIntAndNvarchar()
    {
        Assert.Equal(
            new byte[] { 0, 0, 0, 7 },
            SqlServerStableKeyCodec.Encode(
                new StableKey([new KeyComponent("id", 7)]),
                SqlServerTable(("id", SqlDbType.Int))
            )
        );
        Assert.Equal(
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 7 },
            SqlServerStableKeyCodec.Encode(
                new StableKey([new KeyComponent("id", 7L)]),
                SqlServerTable(("id", SqlDbType.BigInt))
            )
        );
        Assert.Equal(
            new byte[] { 0, 0, 0, 2, 0x61, 0x62 },
            SqlServerStableKeyCodec.Encode(
                new StableKey([new KeyComponent("id", "ab")]),
                SqlServerTable(("id", SqlDbType.NVarChar))
            )
        );
    }

    [Fact]
    public void PostgreSql_KeepsTheOriginalLayoutForIntegerBigintAndText()
    {
        Assert.Equal(
            new byte[] { 0, 0, 0, 7 },
            PostgreSqlStableKeyCodec.Encode(
                new StableKey([new KeyComponent("id", 7)]),
                PostgreSqlTable(("id", NpgsqlDbType.Integer))
            )
        );
        Assert.Equal(
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 7 },
            PostgreSqlStableKeyCodec.Encode(
                new StableKey([new KeyComponent("id", 7L)]),
                PostgreSqlTable(("id", NpgsqlDbType.Bigint))
            )
        );
        Assert.Equal(
            new byte[] { 0, 0, 0, 2, 0x61, 0x62 },
            PostgreSqlStableKeyCodec.Encode(
                new StableKey([new KeyComponent("id", "ab")]),
                PostgreSqlTable(("id", NpgsqlDbType.Text))
            )
        );
    }

    [Fact]
    public void SqlServer_RoundTripsACompositeKeyMixingIntAndVarchar()
    {
        var table = SqlServerTable(("tenant", SqlDbType.Int), ("code", SqlDbType.VarChar), ("issued", SqlDbType.Date));
        var key = new StableKey([
            new KeyComponent("tenant", 3),
            new KeyComponent("code", "REF-1"),
            new KeyComponent("issued", new DateTime(2024, 1, 2)),
        ]);

        var decoded = SqlServerStableKeyCodec.Decode(SqlServerStableKeyCodec.Encode(key, table), table);

        Assert.Equal(key, decoded);
        Assert.IsType<int>(decoded.Components[0].Value);
        Assert.IsType<string>(decoded.Components[1].Value);
        Assert.IsType<DateTime>(decoded.Components[2].Value);
    }

    [Fact]
    public void PostgreSql_RoundTripsACompositeKeyMixingIntegerAndVarchar()
    {
        var table = PostgreSqlTable(
            ("tenant", NpgsqlDbType.Integer),
            ("code", NpgsqlDbType.Varchar),
            ("id", NpgsqlDbType.Uuid)
        );
        var key = new StableKey([
            new KeyComponent("tenant", 3),
            new KeyComponent("code", "REF-1"),
            new KeyComponent("id", Guid.NewGuid()),
        ]);

        var decoded = PostgreSqlStableKeyCodec.Decode(PostgreSqlStableKeyCodec.Encode(key, table), table);

        Assert.Equal(key, decoded);
        Assert.IsType<int>(decoded.Components[0].Value);
        Assert.IsType<string>(decoded.Components[1].Value);
        Assert.IsType<Guid>(decoded.Components[2].Value);
    }

    [Fact]
    public void ApproximateNumbersStayUnsupportedOnBothProviders()
    {
        Assert.False(SqlServerStableKeyCodec.Supports(SqlDbType.Float));
        Assert.False(SqlServerStableKeyCodec.Supports(SqlDbType.Real));
        Assert.False(SqlServerStableKeyCodec.Supports(SqlDbType.Xml));
        Assert.False(PostgreSqlStableKeyCodec.Supports(NpgsqlDbType.Double));
        Assert.False(PostgreSqlStableKeyCodec.Supports(NpgsqlDbType.Real));
        Assert.False(PostgreSqlStableKeyCodec.Supports(NpgsqlDbType.Json));
        Assert.Throws<NotSupportedException>(() =>
            SqlServerStableKeyCodec.Encode(
                new StableKey([new KeyComponent("id", 1.5)]),
                SqlServerTable(("id", SqlDbType.Float))
            )
        );
        Assert.Throws<NotSupportedException>(() =>
            PostgreSqlStableKeyCodec.Encode(
                new StableKey([new KeyComponent("id", 1.5)]),
                PostgreSqlTable(("id", NpgsqlDbType.Double))
            )
        );
    }

    private static SqlServerWriteTable SqlServerTable(params (string Name, SqlDbType Type)[] columns) =>
        new(
            new TableAddress("dbo", "keys"),
            columns.Select(column => new SqlServerWriteColumn(
                column.Name,
                column.Type.ToString().ToLowerInvariant(),
                typeof(object),
                column.Type,
                true,
                false,
                false,
                false,
                false,
                null
            ))
        );

    private static PostgreSqlWriteTable PostgreSqlTable(params (string Name, NpgsqlDbType Type)[] columns) =>
        new(
            new TableAddress("public", "keys"),
            columns.Select(column => new PostgreSqlWriteColumn(
                column.Name,
                column.Type.ToString().ToLowerInvariant(),
                column.Type,
                true,
                false,
                false,
                false,
                null
            ))
        );
}
