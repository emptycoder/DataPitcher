using DataPitcher.Core.Schema;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed record SqlServerColumn(string Name, string StoreType, Type ClrType, bool IsNullable);

public sealed record SqlServerTable(TableDefinition Definition, IReadOnlyList<SqlServerColumn> Columns)
{
    public SqlServerColumn Column(string name) => Columns.Single(c => c.Name == name);
}

public sealed class SqlServerSchemaSnapshot
{
    public SqlServerSchemaSnapshot(IEnumerable<SqlServerTable> tables, IEnumerable<ForeignKeyDefinition> foreignKeys)
    {
        Tables = tables.ToArray();
        ForeignKeys = foreignKeys.ToArray();
    }

    public IReadOnlyList<SqlServerTable> Tables { get; }
    public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; }

    public SqlServerTable Table(string name) => Tables.Single(t => t.Definition.Name == name);
    public ForeignKeyDefinition ForeignKey(string name) => ForeignKeys.Single(f => f.Name == name);
}

public sealed class SqlServerCatalogReader(string connectionString)
{
    private const string ColumnsSql =
        "/* DataPitcher.Catalog.Columns */ SELECT t.name, c.name, ty.name, c.max_length, c.is_nullable " +
        "FROM sys.tables t " +
        "JOIN sys.schemas s ON s.schema_id = t.schema_id " +
        "JOIN sys.columns c ON c.object_id = t.object_id " +
        "JOIN sys.types ty ON ty.user_type_id = c.user_type_id " +
        "WHERE s.name = @schema " +
        "ORDER BY t.name, c.column_id";

    private const string KeysSql =
        "/* DataPitcher.Catalog.Keys */ SELECT t.name, k.name, k.type, c.name, i.key_ordinal " +
        "FROM sys.key_constraints k " +
        "JOIN sys.tables t ON t.object_id = k.parent_object_id " +
        "JOIN sys.schemas s ON s.schema_id = t.schema_id " +
        "JOIN sys.index_columns i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id " +
        "JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = i.column_id " +
        "WHERE s.name = @schema AND i.key_ordinal > 0 " +
        "ORDER BY t.name, k.name, i.key_ordinal";

    private const string ForeignKeysSql =
        "/* DataPitcher.Catalog.ForeignKeys */ SELECT f.name, ct.name, pt.name, cc.name, pc.name, x.constraint_column_id, f.is_disabled, f.is_not_trusted " +
        "FROM sys.foreign_keys f " +
        "JOIN sys.tables ct ON ct.object_id = f.parent_object_id " +
        "JOIN sys.schemas s ON s.schema_id = ct.schema_id " +
        "JOIN sys.tables pt ON pt.object_id = f.referenced_object_id " +
        "JOIN sys.foreign_key_columns x ON x.constraint_object_id = f.object_id " +
        "JOIN sys.columns cc ON cc.object_id = x.parent_object_id AND cc.column_id = x.parent_column_id " +
        "JOIN sys.columns pc ON pc.object_id = x.referenced_object_id AND pc.column_id = x.referenced_column_id " +
        "WHERE s.name = @schema " +
        "ORDER BY f.object_id, x.constraint_column_id";

    public async Task<SqlServerSchemaSnapshot> ReadAsync(string schema, CancellationToken ct)
    {
        var columns = await ReadColumnsAsync(schema, ct);
        var keys = await ReadKeysAsync(schema, columns.Keys, ct);
        var definitions = columns.ToDictionary(
            x => x.Key,
            x => new TableDefinition(schema, x.Key, x.Value.Select(c => new ColumnDefinition(c.Name, c.ClrType, c.IsNullable)).ToArray(), keys[x.Key].Primary, keys[x.Key].Unique));
        var tables = definitions.Values.Select(d => new SqlServerTable(d, columns[d.Name]));
        var foreignKeys = await ReadForeignKeysAsync(schema, definitions, ct);
        return new SqlServerSchemaSnapshot(tables, foreignKeys);
    }

    private async Task<Dictionary<string, List<SqlServerColumn>>> ReadColumnsAsync(string schema, CancellationToken ct)
    {
        var columns = new Dictionary<string, List<SqlServerColumn>>();
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, ColumnsSql, schema);
        await using var rows = await command.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            var table = rows.GetString(0);
            if (!columns.TryGetValue(table, out var list))
                columns[table] = list = [];
            var typeName = rows.GetString(2);
            list.Add(new SqlServerColumn(rows.GetString(1), StoreType(typeName, rows.GetInt16(3)), Map(typeName), rows.GetBoolean(4)));
        }

        return columns;
    }

    private async Task<Dictionary<string, (UniqueConstraint? Primary, List<UniqueConstraint> Unique)>> ReadKeysAsync(
        string schema, IEnumerable<string> tables, CancellationToken ct)
    {
        var keys = tables.ToDictionary(name => name, _ => ((UniqueConstraint?)null, new List<UniqueConstraint>()));
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, KeysSql, schema);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var groups = new List<(string Table, string Name, string Type, List<string> Columns)>();
        while (await rows.ReadAsync(ct))
        {
            var group = groups.LastOrDefault(x => x.Table == rows.GetString(0) && x.Name == rows.GetString(1));
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
            keys[group.Table] = group.Type == "PK" ? (constraint, prior.Item2) : (prior.Item1, [.. prior.Item2, constraint]);
        }

        return keys;
    }

    private async Task<List<ForeignKeyDefinition>> ReadForeignKeysAsync(
        string schema, IReadOnlyDictionary<string, TableDefinition> tables, CancellationToken ct)
    {
        var groups = new List<(string Name, string Child, string Parent, List<string> ChildColumns, List<string> ParentColumns, bool Disabled, bool NotTrusted)>();
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, ForeignKeysSql, schema);
        await using var rows = await command.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            var group = groups.LastOrDefault(x => x.Name == rows.GetString(0));
            if (group.ChildColumns is null)
            {
                group = (rows.GetString(0), rows.GetString(1), rows.GetString(2), [], [], rows.GetBoolean(6), rows.GetBoolean(7));
                groups.Add(group);
            }

            group.ChildColumns.Add(rows.GetString(3));
            group.ParentColumns.Add(rows.GetString(4));
        }

        return groups.Select(g => new ForeignKeyDefinition(g.Name, tables[g.Child], tables[g.Parent], g.ChildColumns, g.ParentColumns, !g.Disabled, !g.NotTrusted)).ToList();
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

    private static Type Map(string type) => type switch
    {
        "int" => typeof(int),
        "nvarchar" => typeof(string),
        _ => throw new NotSupportedException($"SQL Server type '{type}' is not mapped.")
    };

    private static string StoreType(string type, short length) => type == "nvarchar" ? $"nvarchar({length / 2})" : type;
}
