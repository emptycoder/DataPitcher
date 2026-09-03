using DataPitcher.Core.Plans;
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

    private static NpgsqlDbType Map(string type) =>
        type switch
        {
            "int4" => NpgsqlDbType.Integer,
            "int8" => NpgsqlDbType.Bigint,
            "text" => NpgsqlDbType.Text,
            "uuid" => NpgsqlDbType.Uuid,
            _ => throw new NotSupportedException($"PostgreSQL transfer column type '{type}' is not supported."),
        };
}
