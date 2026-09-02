using DataPitcher.Core.Schema;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed record PostgreSqlTable(TableDefinition Definition)
{
    public ColumnDefinition Column(string name) => Definition.Columns.Single(x => x.Name == name);
}

public sealed class PostgreSqlSchemaSnapshot
{
    public PostgreSqlSchemaSnapshot(IEnumerable<PostgreSqlTable> tables, IEnumerable<ForeignKeyDefinition> foreignKeys)
    {
        Tables = tables.ToArray();
        ForeignKeys = foreignKeys.ToArray();
    }

    public IReadOnlyList<PostgreSqlTable> Tables { get; }
    public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; }

    public PostgreSqlTable Table(string name) => Tables.Single(x => x.Definition.Name == name);
    public ForeignKeyDefinition ForeignKey(string name) => ForeignKeys.Single(x => x.Name == name);
}

public sealed class PostgreSqlCatalogReader(NpgsqlDataSource dataSource)
{
    private const string ColumnsSql =
        "SELECT c.relname, a.attname, t.typname, NOT a.attnotnull " +
        "FROM pg_class c " +
        "JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "JOIN pg_attribute a ON a.attrelid = c.oid " +
        "JOIN pg_type t ON t.oid = a.atttypid " +
        "WHERE n.nspname = @schema AND c.relkind IN ('r','p') AND a.attnum > 0 AND NOT a.attisdropped " +
        "ORDER BY c.relname, a.attnum";

    private const string KeysSql =
        "SELECT c.relname, con.conname, con.contype::text, array_agg(a.attname ORDER BY k.ordinality) " +
        "FROM pg_constraint con " +
        "JOIN pg_class c ON c.oid = con.conrelid " +
        "JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "JOIN unnest(con.conkey) WITH ORDINALITY k(attnum, ordinality) ON true " +
        "JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum " +
        "WHERE n.nspname = @schema AND con.contype IN ('p','u') " +
        "GROUP BY c.relname, con.conname, con.contype";

    private const string ForeignKeysSql =
        "SELECT con.conname, c.relname, p.relname, " +
        "array_agg(ca.attname ORDER BY ck.ordinality), array_agg(pa.attname ORDER BY ck.ordinality), " +
        "COALESCE((SELECT bool_and(tr.tgenabled <> 'D') FROM pg_trigger tr WHERE tr.tgconstraint = con.oid), true), " +
        "con.convalidated " +
        "FROM pg_constraint con " +
        "JOIN pg_class c ON c.oid = con.conrelid " +
        "JOIN pg_class p ON p.oid = con.confrelid " +
        "JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "JOIN unnest(con.conkey) WITH ORDINALITY ck(attnum, ordinality) ON true " +
        "JOIN unnest(con.confkey) WITH ORDINALITY pk(attnum, ordinality) ON pk.ordinality = ck.ordinality " +
        "JOIN pg_attribute ca ON ca.attrelid = c.oid AND ca.attnum = ck.attnum " +
        "JOIN pg_attribute pa ON pa.attrelid = p.oid AND pa.attnum = pk.attnum " +
        "WHERE n.nspname = @schema AND con.contype = 'f' " +
        "GROUP BY con.oid, con.conname, c.relname, p.relname, con.convalidated";

    public async Task<PostgreSqlSchemaSnapshot> ReadAsync(string schema, CancellationToken ct)
    {
        var columns = await ReadColumnsAsync(schema, ct);
        var keys = await ReadKeysAsync(schema, columns.Keys, ct);
        var definitions = columns.ToDictionary(
            x => x.Key,
            x => new TableDefinition(schema, x.Key, x.Value, keys[x.Key].Primary, keys[x.Key].Unique));
        var tables = definitions.Values.Select(x => new PostgreSqlTable(x)).ToArray();
        var foreignKeys = await ReadForeignKeysAsync(schema, definitions, ct);
        return new PostgreSqlSchemaSnapshot(tables, foreignKeys);
    }

    private async Task<Dictionary<string, List<ColumnDefinition>>> ReadColumnsAsync(string schema, CancellationToken ct)
    {
        var columns = new Dictionary<string, List<ColumnDefinition>>();
        await using var command = Command(ColumnsSql, schema);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            if (!columns.TryGetValue(table, out var list))
                columns[table] = list = [];
            list.Add(new ColumnDefinition(reader.GetString(1), Map(reader.GetString(2)), reader.GetBoolean(3)));
        }

        return columns;
    }

    private async Task<Dictionary<string, (UniqueConstraint? Primary, List<UniqueConstraint> Unique)>> ReadKeysAsync(
        string schema, IEnumerable<string> tables, CancellationToken ct)
    {
        var keys = tables.ToDictionary(name => name, _ => ((UniqueConstraint?)null, new List<UniqueConstraint>()));
        await using var command = Command(KeysSql, schema);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            var constraint = new UniqueConstraint(reader.GetString(1), reader.GetFieldValue<string[]>(3));
            var entry = keys[table];
            keys[table] = reader.GetString(2) == "p"
                ? (constraint, entry.Item2)
                : (entry.Item1, [.. entry.Item2, constraint]);
        }

        return keys;
    }

    private async Task<List<ForeignKeyDefinition>> ReadForeignKeysAsync(
        string schema, IReadOnlyDictionary<string, TableDefinition> tables, CancellationToken ct)
    {
        var foreignKeys = new List<ForeignKeyDefinition>();
        await using var command = Command(ForeignKeysSql, schema);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            foreignKeys.Add(new ForeignKeyDefinition(
                reader.GetString(0),
                tables[reader.GetString(1)],
                tables[reader.GetString(2)],
                reader.GetFieldValue<string[]>(3),
                reader.GetFieldValue<string[]>(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6)));
        }

        return foreignKeys;
    }

    private NpgsqlCommand Command(string sql, string schema)
    {
        var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("schema", schema);
        return command;
    }

    private static Type Map(string typeName) => typeName switch
    {
        "int4" => typeof(int),
        "text" => typeof(string),
        _ => throw new NotSupportedException($"PostgreSQL type '{typeName}' is not mapped.")
    };
}
