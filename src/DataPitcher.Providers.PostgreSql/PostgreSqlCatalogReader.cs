using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed record PostgreSqlTable(TableDefinition Definition)
{
    public ColumnDefinition Column(string name) =>
        Definition.Columns.Single(x => string.Equals(x.Name, name, StringComparison.Ordinal));
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

    public PostgreSqlTable Table(string schema, string name) =>
        Tables.Single(x =>
            string.Equals(x.Definition.Schema, schema, StringComparison.Ordinal)
            && string.Equals(x.Definition.Name, name, StringComparison.Ordinal)
        );

    /// <summary>Lookup by name alone; unambiguous only while a single schema is loaded.</summary>
    public PostgreSqlTable Table(string name) =>
        Tables.Single(x => string.Equals(x.Definition.Name, name, StringComparison.Ordinal));

    public ForeignKeyDefinition ForeignKey(string name) =>
        ForeignKeys.Single(x => string.Equals(x.Name, name, StringComparison.Ordinal));
}

public sealed class PostgreSqlCatalogReader(NpgsqlDataSource dataSource)
{
    private const string ColumnsSql =
        "/* DataPitcher.Catalog.Columns */ SELECT c.relname, a.attname, t.typname, NOT a.attnotnull, a.attgenerated <> '' "
        + "FROM pg_class c "
        + "JOIN pg_namespace n ON n.oid = c.relnamespace "
        + "JOIN pg_attribute a ON a.attrelid = c.oid "
        + "JOIN pg_type t ON t.oid = a.atttypid "
        + "WHERE n.nspname = @schema AND c.relkind IN ('r','p') AND a.attnum > 0 AND NOT a.attisdropped "
        + "ORDER BY c.relname, a.attnum";

    private const string KeysSql =
        "/* DataPitcher.Catalog.Keys */ SELECT c.relname, con.conname, con.contype::text, array_agg(a.attname ORDER BY k.ordinality) "
        + "FROM pg_constraint con "
        + "JOIN pg_class c ON c.oid = con.conrelid "
        + "JOIN pg_namespace n ON n.oid = c.relnamespace "
        + "JOIN pg_namespace pn ON pn.oid = p.relnamespace "
        + "JOIN unnest(con.conkey) WITH ORDINALITY k(attnum, ordinality) ON true "
        + "JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum "
        + "WHERE n.nspname = @schema AND con.contype IN ('p','u') "
        + "GROUP BY c.relname, con.conname, con.contype";

    private const string ForeignKeysSql =
        "/* DataPitcher.Catalog.ForeignKeys */ SELECT con.conname, n.nspname, c.relname, pn.nspname, p.relname, "
        + "array_agg(ca.attname ORDER BY ck.ordinality), array_agg(pa.attname ORDER BY ck.ordinality), "
        + "COALESCE((SELECT bool_and(tr.tgenabled <> 'D') FROM pg_trigger tr WHERE tr.tgconstraint = con.oid), true), "
        + "con.convalidated "
        + "FROM pg_constraint con "
        + "JOIN pg_class c ON c.oid = con.conrelid "
        + "JOIN pg_class p ON p.oid = con.confrelid "
        + "JOIN pg_namespace n ON n.oid = c.relnamespace "
        + "JOIN pg_namespace pn ON pn.oid = p.relnamespace "
        + "JOIN unnest(con.conkey) WITH ORDINALITY ck(attnum, ordinality) ON true "
        + "JOIN unnest(con.confkey) WITH ORDINALITY pk(attnum, ordinality) ON pk.ordinality = ck.ordinality "
        + "JOIN pg_attribute ca ON ca.attrelid = c.oid AND ca.attnum = ck.attnum "
        + "JOIN pg_attribute pa ON pa.attrelid = p.oid AND pa.attnum = pk.attnum "
        + "WHERE n.nspname = @schema AND con.contype = 'f' "
        + "GROUP BY con.oid, con.conname, n.nspname, c.relname, pn.nspname, p.relname, con.convalidated";

    /// <summary>
    /// Reads <paramref name="schema"/> and, transitively, every schema its tables reference through foreign keys, so
    /// cross-schema parents are part of the snapshot and the dependency closure.
    /// </summary>
    public async Task<PostgreSqlSchemaSnapshot> ReadAsync(string schema, CancellationToken ct)
    {
        var definitions = new Dictionary<(string Schema, string Name), TableDefinition>();
        var rawForeignKeys = new List<RawForeignKey>();
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([schema]);
        while (pending.TryDequeue(out var current))
        {
            if (!loaded.Add(current))
                continue;
            var columns = await ReadColumnsAsync(current, ct);
            var keys = await ReadKeysAsync(current, columns.Keys, ct);
            foreach (var (name, tableColumns) in columns)
                definitions[(current, name)] = new TableDefinition(
                    current,
                    name,
                    tableColumns,
                    keys[name].Primary,
                    keys[name].Unique
                );
            var foreignKeys = await ReadForeignKeysAsync(current, ct);
            rawForeignKeys.AddRange(foreignKeys);
            foreach (var foreignKey in foreignKeys)
                if (!loaded.Contains(foreignKey.ParentSchema))
                    pending.Enqueue(foreignKey.ParentSchema);
        }
        var tables = definitions
            .Values.OrderBy(x => x.Schema, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new PostgreSqlTable(x))
            .ToArray();
        var resolved = rawForeignKeys
            .Where(f =>
                definitions.ContainsKey((f.ChildSchema, f.Child)) && definitions.ContainsKey((f.ParentSchema, f.Parent))
            )
            .Select(f => new ForeignKeyDefinition(
                f.Name,
                definitions[(f.ChildSchema, f.Child)],
                definitions[(f.ParentSchema, f.Parent)],
                f.ChildColumns,
                f.ParentColumns,
                f.Enabled,
                f.Validated
            ));
        return new PostgreSqlSchemaSnapshot(tables, resolved);
    }

    private sealed record RawForeignKey(
        string Name,
        string ChildSchema,
        string Child,
        string ParentSchema,
        string Parent,
        string[] ChildColumns,
        string[] ParentColumns,
        bool Enabled,
        bool Validated
    );

    private async Task<Dictionary<string, List<ColumnDefinition>>> ReadColumnsAsync(string schema, CancellationToken ct)
    {
        var columns = new Dictionary<string, List<ColumnDefinition>>(StringComparer.Ordinal);
        await using var command = Command(ColumnsSql, schema);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            if (!columns.TryGetValue(table, out var list))
                columns[table] = list = [];
            list.Add(
                new ColumnDefinition(
                    reader.GetString(1),
                    Map(reader.GetString(2)),
                    reader.GetBoolean(3),
                    reader.GetBoolean(4)
                )
            );
        }

        return columns;
    }

    private async Task<Dictionary<string, (UniqueConstraint? Primary, List<UniqueConstraint> Unique)>> ReadKeysAsync(
        string schema,
        IEnumerable<string> tables,
        CancellationToken ct
    )
    {
        var keys = tables.ToDictionary(
            name => name,
            _ => ((UniqueConstraint?)null, new List<UniqueConstraint>()),
            StringComparer.Ordinal
        );
        await using var command = Command(KeysSql, schema);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            var constraint = new UniqueConstraint(reader.GetString(1), reader.GetFieldValue<string[]>(3));
            var entry = keys[table];
            keys[table] = string.Equals(reader.GetString(2), "p", StringComparison.Ordinal)
                ? (constraint, entry.Item2)
                : (entry.Item1, [.. entry.Item2, constraint]);
        }

        return keys;
    }

    private async Task<List<RawForeignKey>> ReadForeignKeysAsync(string schema, CancellationToken ct)
    {
        var foreignKeys = new List<RawForeignKey>();
        await using var command = Command(ForeignKeysSql, schema);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            foreignKeys.Add(
                new RawForeignKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetFieldValue<string[]>(5),
                    reader.GetFieldValue<string[]>(6),
                    reader.GetBoolean(7),
                    reader.GetBoolean(8)
                )
            );
        return foreignKeys;
    }

    private NpgsqlCommand Command(string sql, string schema)
    {
        var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("schema", schema);
        return command;
    }

    /// <summary>
    /// CLR type used to move values of a PostgreSQL column. Arrays, ranges, geometric and other exotic types map to
    /// <see cref="object"/> so a scan never fails on them.
    /// </summary>
    internal static Type Map(string typeName) =>
        typeName switch
        {
            "int8" => typeof(long),
            "int4" or "oid" => typeof(int),
            "int2" => typeof(short),
            "bool" => typeof(bool),
            "numeric" or "money" => typeof(decimal),
            "float8" => typeof(double),
            "float4" => typeof(float),
            "text" or "varchar" or "bpchar" or "char" or "name" or "citext" or "json" or "jsonb" or "xml" =>
                typeof(string),
            "uuid" => typeof(Guid),
            "bytea" => typeof(byte[]),
            "date" => typeof(DateOnly),
            "time" => typeof(TimeOnly),
            "timestamp" => typeof(DateTime),
            "timestamptz" => typeof(DateTimeOffset),
            "interval" => typeof(TimeSpan),
            _ => typeof(object),
        };
}
