using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;
using NpgsqlTypes;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlTransferSchemaReader(NpgsqlDataSource dataSource)
{
    public async Task<PostgreSqlWriteTable> ReadAsync(
        string schema,
        string table,
        IReadOnlyCollection<string> stableKeys,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            "SELECT a.attname,format_type(a.atttypid,a.atttypmod),t.typname,a.attgenerated::text,a.attidentity::text,co.collname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid JOIN pg_type t ON t.oid=a.atttypid LEFT JOIN pg_collation co ON co.oid=a.attcollation WHERE n.nspname=@schema AND c.relname=@table AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<PostgreSqlWriteColumn>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = Map(reader.GetString(2));
            var name = reader.GetString(0);
            columns.Add(
                new(
                    name,
                    reader.GetString(1),
                    type,
                    stableKeys.Contains(name, StringComparer.Ordinal),
                    reader.GetString(3) == "s",
                    false,
                    reader.GetString(4) == "a",
                    reader.IsDBNull(5) ? null : reader.GetString(5)
                )
            );
        }
        return new PostgreSqlWriteTable(new TableAddress(schema, table), columns);
    }

    /// <summary>Provider type used to bind and copy values; the declared store type still drives staging DDL.</summary>
    internal static NpgsqlDbType Map(string type) =>
        type switch
        {
            "int2" => NpgsqlDbType.Smallint,
            "int4" or "oid" => NpgsqlDbType.Integer,
            "int8" => NpgsqlDbType.Bigint,
            "bool" => NpgsqlDbType.Boolean,
            "numeric" => NpgsqlDbType.Numeric,
            "money" => NpgsqlDbType.Money,
            "float4" => NpgsqlDbType.Real,
            "float8" => NpgsqlDbType.Double,
            "text" or "citext" => NpgsqlDbType.Text,
            "varchar" => NpgsqlDbType.Varchar,
            "bpchar" or "char" => NpgsqlDbType.Char,
            "name" => NpgsqlDbType.Name,
            "json" => NpgsqlDbType.Json,
            "jsonb" => NpgsqlDbType.Jsonb,
            "xml" => NpgsqlDbType.Xml,
            "uuid" => NpgsqlDbType.Uuid,
            "bytea" => NpgsqlDbType.Bytea,
            "date" => NpgsqlDbType.Date,
            "time" => NpgsqlDbType.Time,
            "timetz" => NpgsqlDbType.TimeTz,
            "timestamp" => NpgsqlDbType.Timestamp,
            "timestamptz" => NpgsqlDbType.TimestampTz,
            "interval" => NpgsqlDbType.Interval,
            "inet" => NpgsqlDbType.Inet,
            "macaddr" => NpgsqlDbType.MacAddr,
            _ => throw new NotSupportedException($"PostgreSQL transfer column type '{type}' is not supported."),
        };
}
