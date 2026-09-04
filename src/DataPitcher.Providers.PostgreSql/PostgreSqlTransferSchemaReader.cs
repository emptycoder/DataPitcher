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
        // Matched without regard to case; the table's own spelling comes back with it and is what SQL then quotes.
        const string sql =
            "SELECT a.attname,format_type(a.atttypid,a.atttypmod),t.typname,a.attgenerated::text,a.attidentity::text,co.collname,n.nspname,c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid JOIN pg_type t ON t.oid=a.atttypid LEFT JOIN pg_collation co ON co.oid=a.attcollation WHERE lower(n.nspname)=lower(@schema) AND lower(c.relname)=lower(@table) AND c.relkind IN ('r','p') AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<PostgreSqlWriteColumn>();
        var actual = new TableAddress(schema, table);
        while (await reader.ReadAsync(cancellationToken))
        {
            actual = new TableAddress(reader.GetString(6), reader.GetString(7));
            var type = Map(reader.GetString(2));
            var name = reader.GetString(0);
            columns.Add(
                new(
                    name,
                    reader.GetString(1),
                    type,
                    stableKeys.Contains(name, DatabaseNames.Comparer),
                    reader.GetString(3) == "s",
                    false,
                    reader.GetString(4) == "a",
                    reader.IsDBNull(5) ? null : reader.GetString(5)
                )
            );
        }
        await reader.CloseAsync();
        if (columns.Count == 0)
            throw new InvalidOperationException(
                $"Table {schema}.{table} is not visible as an ordinary or partitioned table: it does not exist under that name, or it is a view, materialized view or foreign table rather than a table."
            );
        var missing = stableKeys.Where(key => !columns.Any(column => DatabaseNames.Equals(column.Name, key))).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"Table {schema}.{table} has no column named {string.Join(", ", missing)}, which the stable key ({string.Join(", ", stableKeys)}) requires."
            );
        return new PostgreSqlWriteTable(
            actual,
            columns,
            await UniqueKeysAsync(actual.Schema, actual.Name, cancellationToken)
        );
    }

    /// <summary>Column sets of every plain unique constraint or index on the table, key columns in index order.</summary>
    private async Task<IReadOnlyList<IReadOnlyList<string>>> UniqueKeysAsync(
        string schema,
        string table,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            "SELECT i.indexrelid::int,a.attname FROM pg_index i JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum,ord) ON true JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum WHERE i.indrelid=@target::regclass AND i.indisunique AND i.indpred IS NULL AND 0 <> ALL(i.indkey::int[]) ORDER BY i.indexrelid,k.ord";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("target", PostgreSqlIdentifier.Qualified(schema, table));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new Dictionary<int, List<string>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!keys.TryGetValue(reader.GetInt32(0), out var columns))
                keys[reader.GetInt32(0)] = columns = [];
            columns.Add(reader.GetString(1));
        }
        return keys.OrderBy(pair => pair.Key).Select(pair => (IReadOnlyList<string>)pair.Value).ToArray();
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
