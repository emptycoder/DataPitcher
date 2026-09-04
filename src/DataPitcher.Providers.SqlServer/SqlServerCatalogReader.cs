using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed record SqlServerColumn(string Name, string StoreType, Type ClrType, bool IsNullable, bool IsGenerated);

public sealed record SqlServerTable(TableDefinition Definition, IReadOnlyList<SqlServerColumn> Columns)
{
    public SqlServerColumn Column(string name) =>
        Columns.Single(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}

public sealed class SqlServerSchemaSnapshot
{
    public SqlServerSchemaSnapshot(
        IEnumerable<SqlServerTable> tables,
        IEnumerable<ForeignKeyDefinition> foreignKeys,
        IEnumerable<UnresolvedForeignKey>? unresolvedForeignKeys = null
    )
    {
        Tables = tables.ToArray();
        ForeignKeys = foreignKeys.ToArray();
        UnresolvedForeignKeys = (unresolvedForeignKeys ?? []).ToArray();
    }

    public IReadOnlyList<SqlServerTable> Tables { get; }
    public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; }

    /// <summary>Foreign keys whose parent table the login cannot see; the graph has no edge for them.</summary>
    public IReadOnlyList<UnresolvedForeignKey> UnresolvedForeignKeys { get; }

    public SqlServerTable Table(string schema, string name) =>
        Tables.Single(t =>
            string.Equals(t.Definition.Schema, schema, StringComparison.Ordinal)
            && string.Equals(t.Definition.Name, name, StringComparison.Ordinal)
        );

    /// <summary>Lookup by name alone; unambiguous only while a single schema is loaded.</summary>
    public SqlServerTable Table(string name) =>
        Tables.Single(t => string.Equals(t.Definition.Name, name, StringComparison.Ordinal));

    public ForeignKeyDefinition ForeignKey(string name) =>
        ForeignKeys.Single(f => string.Equals(f.Name, name, StringComparison.Ordinal));
}

public sealed class SqlServerCatalogReader(string connectionString)
{
    static SqlServerCatalogReader() => SqlServerEntraAuthentication.EnsureRegistered();

    private const string ColumnsSql =
        "/* DataPitcher.Catalog.Columns */ SELECT t.name, c.name, ty.name, c.max_length, c.is_nullable, CAST(CASE WHEN cc.is_computed = 1 OR c.generated_always_type <> 0 THEN 1 ELSE 0 END AS bit), c.precision, c.scale "
        + "FROM sys.tables t "
        + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
        + "JOIN sys.columns c ON c.object_id = t.object_id "
        + "JOIN sys.types ty ON ty.user_type_id = c.user_type_id "
        + "LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id "
        + "WHERE s.name = @schema "
        + "ORDER BY t.name, c.column_id";

    private const string KeysSql =
        "/* DataPitcher.Catalog.Keys */ SELECT t.name, k.name, k.type, c.name, i.key_ordinal "
        + "FROM sys.key_constraints k "
        + "JOIN sys.tables t ON t.object_id = k.parent_object_id "
        + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
        + "JOIN sys.index_columns i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id "
        + "JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = i.column_id "
        + "WHERE s.name = @schema AND i.key_ordinal > 0 "
        + "ORDER BY t.name, k.name, i.key_ordinal";

    // The parent side is outer-joined: sys.tables is permission-filtered, and a foreign key whose parent the login
    // cannot see must still be reported (as unresolved) rather than vanish. Its parent is then named by object id.
    private const string ForeignKeysSql =
        "/* DataPitcher.Catalog.ForeignKeys */ SELECT f.name, s.name, ct.name, COALESCE(ps.name, OBJECT_SCHEMA_NAME(f.referenced_object_id), N'?'), COALESCE(pt.name, OBJECT_NAME(f.referenced_object_id), N'#' + CAST(f.referenced_object_id AS nvarchar(20))), cc.name, pc.name, x.constraint_column_id, f.is_disabled, f.is_not_trusted "
        + "FROM sys.foreign_keys f "
        + "JOIN sys.tables ct ON ct.object_id = f.parent_object_id "
        + "JOIN sys.schemas s ON s.schema_id = ct.schema_id "
        + "LEFT JOIN sys.tables pt ON pt.object_id = f.referenced_object_id "
        + "LEFT JOIN sys.schemas ps ON ps.schema_id = pt.schema_id "
        + "JOIN sys.foreign_key_columns x ON x.constraint_object_id = f.object_id "
        + "JOIN sys.columns cc ON cc.object_id = x.parent_object_id AND cc.column_id = x.parent_column_id "
        + "LEFT JOIN sys.columns pc ON pc.object_id = x.referenced_object_id AND pc.column_id = x.referenced_column_id "
        + "WHERE s.name = @schema "
        + "ORDER BY f.object_id, x.constraint_column_id";

    /// <summary>
    /// Reads <paramref name="schema"/> and, transitively, every schema its tables reference through foreign keys, so
    /// cross-schema parents (lookup and reference tables) are part of the snapshot and the dependency closure.
    /// </summary>
    public async Task<SqlServerSchemaSnapshot> ReadAsync(string schema, CancellationToken ct)
    {
        var definitions = new Dictionary<(string Schema, string Name), TableDefinition>();
        var columnsByTable = new Dictionary<(string Schema, string Name), List<SqlServerColumn>>();
        var rawForeignKeys = new List<RawForeignKey>();
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([schema]);
        while (pending.TryDequeue(out var current))
        {
            if (!loaded.Add(current))
                continue;
            // DataPitcher's own plan-scoped key tables live in the business schema of the source; they are not part
            // of the schema being transferred, and letting them into a snapshot would make every seal after the first
            // look like a schema change.
            var columns = (await ReadColumnsAsync(current, ct))
                .Where(pair => !SqlServerStagingTables.IsOwnedStagingTable(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var keys = await ReadKeysAsync(current, columns.Keys, ct);
            foreach (var (name, tableColumns) in columns)
            {
                definitions[(current, name)] = new TableDefinition(
                    current,
                    name,
                    tableColumns
                        .Select(c => new ColumnDefinition(c.Name, c.ClrType, c.IsNullable, c.IsGenerated))
                        .ToArray(),
                    keys[name].Primary,
                    keys[name].Unique
                );
                columnsByTable[(current, name)] = tableColumns;
            }
            var foreignKeys = await ReadForeignKeysAsync(current, ct);
            rawForeignKeys.AddRange(foreignKeys);
            foreach (var foreignKey in foreignKeys)
                if (!loaded.Contains(foreignKey.ParentSchema))
                    pending.Enqueue(foreignKey.ParentSchema);
        }
        var tables = definitions
            .Values.OrderBy(d => d.Schema, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => new SqlServerTable(d, columnsByTable[(d.Schema, d.Name)]));
        // A parent the login cannot see is absent from the catalog (sys.tables is permission-filtered). The foreign
        // key is reported as unresolved rather than dropped, so a plan over the child cannot silently miss an edge
        // the database still enforces.
        var unresolved = rawForeignKeys
            .Where(f => !definitions.ContainsKey((f.ParentSchema, f.Parent)))
            .Select(f => new UnresolvedForeignKey(
                f.Name,
                new SchemaTableAddress(f.ChildSchema, f.Child),
                new SchemaTableAddress(f.ParentSchema, f.Parent)
            ))
            .ToArray();
        var resolved = rawForeignKeys
            .Where(f => definitions.ContainsKey((f.ParentSchema, f.Parent)))
            .Select(f => new ForeignKeyDefinition(
                f.Name,
                definitions[(f.ChildSchema, f.Child)],
                definitions[(f.ParentSchema, f.Parent)],
                f.ChildColumns,
                f.ParentColumns,
                !f.Disabled,
                !f.NotTrusted
            ));
        return new SqlServerSchemaSnapshot(tables, resolved, unresolved);
    }

    private sealed record RawForeignKey(
        string Name,
        string ChildSchema,
        string Child,
        string ParentSchema,
        string Parent,
        List<string> ChildColumns,
        List<string> ParentColumns,
        bool Disabled,
        bool NotTrusted
    );

    private async Task<Dictionary<string, List<SqlServerColumn>>> ReadColumnsAsync(string schema, CancellationToken ct)
    {
        var columns = new Dictionary<string, List<SqlServerColumn>>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, ColumnsSql, schema);
        await using var rows = await command.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            var table = rows.GetString(0);
            if (!columns.TryGetValue(table, out var list))
                columns[table] = list = [];
            var typeName = rows.GetString(2);
            list.Add(
                new SqlServerColumn(
                    rows.GetString(1),
                    StoreType(typeName, rows.GetInt16(3), rows.GetByte(6), rows.GetByte(7)),
                    Map(typeName),
                    rows.GetBoolean(4),
                    rows.GetBoolean(5)
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
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, KeysSql, schema);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var groups = new List<(string Table, string Name, string Type, List<string> Columns)>();
        while (await rows.ReadAsync(ct))
        {
            var group = groups.LastOrDefault(x =>
                string.Equals(x.Table, rows.GetString(0), StringComparison.Ordinal)
                && string.Equals(x.Name, rows.GetString(1), StringComparison.Ordinal)
            );
            if (group.Columns is null)
            {
                group = (rows.GetString(0), rows.GetString(1), rows.GetString(2).TrimEnd(), []);
                groups.Add(group);
            }

            group.Columns.Add(rows.GetString(3));
        }

        foreach (var group in groups)
        {
            var constraint = new UniqueConstraint(group.Name, group.Columns);
            var prior = keys[group.Table];
            keys[group.Table] = string.Equals(group.Type, "PK", StringComparison.Ordinal)
                ? (constraint, prior.Item2)
                : (prior.Item1, [.. prior.Item2, constraint]);
        }

        return keys;
    }

    private async Task<List<RawForeignKey>> ReadForeignKeysAsync(string schema, CancellationToken ct)
    {
        var groups = new List<RawForeignKey>();
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, ForeignKeysSql, schema);
        await using var rows = await command.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            var name = rows.GetString(0);
            var group = groups.LastOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
            if (group is null)
            {
                group = new RawForeignKey(
                    name,
                    rows.GetString(1),
                    rows.GetString(2),
                    rows.GetString(3),
                    rows.GetString(4),
                    [],
                    [],
                    rows.GetBoolean(8),
                    rows.GetBoolean(9)
                );
                groups.Add(group);
            }
            group.ChildColumns.Add(rows.GetString(5));
            // Parent columns are unknown when the parent table is invisible; the key is reported as unresolved.
            group.ParentColumns.Add(rows.IsDBNull(6) ? "?" : rows.GetString(6));
        }
        return groups;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static SqlCommand Command(SqlConnection connection, string sql, string schema)
    {
        var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", schema);
        return command;
    }

    /// <summary>
    /// CLR type used to move values of a SQL Server column. Types without a natural CLR shape (spatial, hierarchyid,
    /// sql_variant, CLR user types) map to <see cref="object"/> so a scan never fails on them; the store type still
    /// records exactly what the column is.
    /// </summary>
    internal static Type Map(string type) =>
        type switch
        {
            "bigint" => typeof(long),
            "int" => typeof(int),
            "smallint" => typeof(short),
            "tinyint" => typeof(byte),
            "bit" => typeof(bool),
            "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
            "float" => typeof(double),
            "real" => typeof(float),
            "date" => typeof(DateOnly),
            "time" => typeof(TimeOnly),
            "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
            "datetimeoffset" => typeof(DateTimeOffset),
            "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext" or "xml" or "sysname" => typeof(string),
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => typeof(byte[]),
            "uniqueidentifier" => typeof(Guid),
            _ => typeof(object),
        };

    /// <summary>The column's declared type as valid DDL, so staging tables can mirror it exactly.</summary>
    internal static string StoreType(string type, short length, byte precision, byte scale) =>
        type switch
        {
            "nvarchar" or "nchar" => length < 0 ? $"{type}(max)" : $"{type}({length / 2})",
            "varchar" or "char" or "varbinary" or "binary" => length < 0 ? $"{type}(max)" : $"{type}({length})",
            "decimal" or "numeric" => $"{type}({precision},{scale})",
            "datetime2" or "datetimeoffset" or "time" => $"{type}({scale})",
            _ => type,
        };
}
