using System.Data;
using System.Security.Cryptography;
using System.Text;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerStagingTables : IAsyncDisposable
{
    private readonly string _source;
    private readonly string _target;
    private readonly SqlServerSchemaSnapshot _schema;
    private readonly IReadOnlyDictionary<TableDefinition, StableKeySelection> _keys;
    private readonly string _plan;
    private readonly bool _dropOnDispose;

    public SqlServerStagingTables(
        string source,
        string target,
        SqlServerSchemaSnapshot schema,
        IReadOnlyDictionary<TableDefinition, StableKeySelection> keys,
        Guid? planId = null,
        bool dropOnDispose = true
    )
    {
        _source = source;
        _target = target;
        _schema = schema;
        _keys = keys;
        _plan = (planId ?? Guid.NewGuid()).ToString("D");
        _dropOnDispose = dropOnDispose;
    }

    public string SourceConnectionString => _source;
    public string TargetConnectionString => _target;

    public string SourceTableName(TableDefinition t) => Name("keys", t);

    public static string SourceTableName(Guid planId, TableAddress table) =>
        Name("keys", planId.ToString("D"), table.Schema, table.Name);

    public string InputTableName(TableDefinition t) => Name("input", t);

    public string TargetTableName(TableDefinition t) => Name("target", t);

    public async Task<IReadOnlyCollection<StableKey>> InsertSourceAsync(
        TableDefinition t,
        IReadOnlyCollection<StableKey> keys,
        int generation,
        CancellationToken ct
    )
    {
        await EnsureAsync(_source, SourceTableName(t), t, ct);
        await ReplaceAsync(_source, InputTableName(t), t, keys, ct);
        return await InsertNewAsync(t, generation, ct);
    }

    public Task ReplaceSourceCandidatesAsync(
        TableDefinition t,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken ct
    ) => ReplaceAsync(_source, InputTableName(t), t, keys, ct);

    public Task ReplaceTargetCandidatesAsync(
        TableDefinition t,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken ct
    ) => ReplaceAsync(_target, TargetTableName(t), t, keys, ct);

    /// <summary>Marks staged keys as part of the transfer set; the transfer reads only marked keys.</summary>
    public async Task MarkIncludedAsync(TableDefinition t, IReadOnlyCollection<StableKey> keys, CancellationToken ct)
    {
        await ReplaceAsync(_source, InputTableName(t), t, keys, ct);
        var join = string.Join(" AND ", Columns(t).Select((_, i) => $"f.[k{i}]=i.[k{i}]"));
        await ExecuteAsync(
            _source,
            $"UPDATE f SET [__included]=1 FROM {Qualified(SourceTableName(t))} f JOIN {Qualified(InputTableName(t))} i ON {join}",
            ct
        );
    }

    public async Task<int> GenerationAsync(TableDefinition t, StableKey key, CancellationToken ct)
    {
        var columns = Columns(t);
        await using var connection = new SqlConnection(_source);
        await connection.OpenAsync(ct);
        var predicate = string.Join(" AND ", columns.Select((_, i) => $"[k{i}]=@p{i}"));
        await using var command = new SqlCommand(
            $"SELECT [__generation] FROM {Qualified(SourceTableName(t))} WHERE {predicate}",
            connection
        );
        for (var i = 0; i < columns.Count; i++)
            command.Parameters.AddWithValue(
                $"@p{i}",
                key.Components.Single(k => DatabaseNames.Equals(k.Column, columns[i])).Value!
            );
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<int> KeyColumnCountInUniqueIndexAsync(TableDefinition t, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_source);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM sys.index_columns WHERE object_id=OBJECT_ID(@table) AND index_id=(SELECT index_id FROM sys.indexes WHERE object_id=OBJECT_ID(@table) AND name=@index)",
            connection
        );
        command.Parameters.AddWithValue("@table", "dbo." + SourceTableName(t));
        command.Parameters.AddWithValue("@index", "UX_" + SourceTableName(t));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>Drops every staging table of the plan so the next closure starts from nothing.</summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        foreach (var t in _keys.Keys)
        {
            await ExecuteAsync(_source, "DROP TABLE IF EXISTS " + Qualified(SourceTableName(t)), ct);
            await ExecuteAsync(_source, "DROP TABLE IF EXISTS " + Qualified(InputTableName(t)), ct);
            await ExecuteAsync(_target, "DROP TABLE IF EXISTS " + Qualified(TargetTableName(t)), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_dropOnDispose)
            return;
        foreach (var t in _keys.Keys)
        {
            await DropAsync(_source, SourceTableName(t));
            await DropAsync(_source, InputTableName(t));
            await DropAsync(_target, TargetTableName(t));
        }
    }

    private string Name(string prefix, TableDefinition table) => Name(prefix, _plan, table.Schema, table.Name);

    private static string Name(string prefix, string plan, string schema, string table) =>
        prefix
        + "_"
        + Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan + "\u001f" + schema + "\u001f" + table)))
            .ToLowerInvariant();

    private IReadOnlyList<string> Columns(TableDefinition t) => _keys[t].Constraint!.Columns;

    public static string Qualified(string name) => SqlServerIdentifier.Qualified("dbo", name);

    /// <summary>Whether a table name is one of DataPitcher's plan-scoped key tables (keys_, input_, target_ + hash).</summary>
    public static bool IsOwnedStagingTable(string name)
    {
        var separator = name.IndexOf('_');
        return separator > 0
            && name[..separator] is "keys" or "input" or "target"
            && name.Length == separator + 65
            && name[(separator + 1)..].All(char.IsAsciiHexDigitLower);
    }

    /// <summary>
    /// Stamps every sealed key of a self-referencing table with its hierarchy level (0 for rows whose parent is
    /// null or outside the sealed keys, then 1, 2, …) stored as a negative generation, so paging by generation
    /// descending writes parents before children. Rows on a cycle keep their closure generation.
    /// </summary>
    public static async Task StampHierarchyAsync(
        string connectionString,
        Guid planId,
        TableDefinition table,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> parentColumns,
        IReadOnlyList<string> referencedColumns,
        CancellationToken ct
    )
    {
        var keys = Qualified(SourceTableName(planId, new TableAddress(table.Schema, table.Name)));
        var source = SqlServerIdentifier.Qualified(table.Schema, table.Name);
        string Key(int i) => "[k" + i + "]";
        string Q(string column) => SqlServerIdentifier.Quote(column);
        var fKeys = string.Join(",", keyColumns.Select((_, i) => "f." + Key(i)));
        var mKeys = string.Join(",", keyColumns.Select((_, i) => Key(i)));
        var joinSource = string.Join(" AND ", keyColumns.Select((column, i) => "s." + Q(column) + "=f." + Key(i)));
        var parentNull = string.Join(" OR ", parentColumns.Select(column => "s." + Q(column) + " IS NULL"));
        // The parent columns name the referenced columns of another row (a unique key, not necessarily the stable
        // key), so one hop through the source table (alias pr) maps a parent reference onto its stable key.
        var joinReferenced = string.Join(
            " AND ",
            parentColumns.Select((column, i) => "pr." + Q(referencedColumns[i]) + "=s." + Q(column))
        );
        // A parent outside the transfer set (never staged, or staged but satisfied by the target) is a root.
        var parentInKeys =
            "EXISTS (SELECT 1 FROM "
            + keys
            + " p JOIN "
            + source
            + " pr ON "
            + string.Join(" AND ", keyColumns.Select((column, i) => "pr." + Q(column) + "=p." + Key(i)))
            + " WHERE p.[__included]=1 AND "
            + joinReferenced
            + ")";
        var joinParentLevel = string.Join(
            " AND ",
            keyColumns.Select((column, i) => "h." + Key(i) + "=pr." + Q(column))
        );
        // A row that is its own parent is a root too, and must not feed the recursion.
        var parentIsSelf =
            "("
            + string.Join(
                " AND ",
                parentColumns.Select((column, i) => "s." + Q(column) + "=s." + Q(referencedColumns[i]))
            )
            + ")";
        var joinLevels = string.Join(" AND ", keyColumns.Select((_, i) => "f." + Key(i) + "=m." + Key(i)));
        var sql =
            ";WITH h AS (SELECT "
            + fKeys
            + ", 0 AS lvl FROM "
            + keys
            + " f JOIN "
            + source
            + " s ON "
            + joinSource
            + " WHERE f.[__included]=1 AND (("
            + parentNull
            + ") OR "
            + parentIsSelf
            + " OR NOT "
            + parentInKeys
            + ") UNION ALL SELECT "
            + fKeys
            + ", h.lvl + 1 FROM "
            + keys
            + " f JOIN "
            + source
            + " s ON "
            + joinSource
            + " JOIN "
            + source
            + " pr ON "
            + joinReferenced
            + " JOIN h ON "
            + joinParentLevel
            + " WHERE f.[__included]=1 AND h.lvl < 4096 AND NOT "
            + parentIsSelf
            + ") "
            // Levels are stored as -(level + 1): 0 and above stays the closure generation of rows the levelling
            // could not reach (rows on a cycle inside the table and everything below them).
            + "UPDATE f SET [__generation] = -(m.lvl + 1) FROM "
            + keys
            + " f JOIN (SELECT "
            + mKeys
            + ", MAX(lvl) AS lvl FROM h GROUP BY "
            + mKeys
            + ") m ON "
            + joinLevels
            + " OPTION (MAXRECURSION 4096);";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task ReplaceAsync(
        string cs,
        string name,
        TableDefinition t,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken ct
    )
    {
        await EnsureAsync(cs, name, t, ct);
        await ExecuteAsync(cs, "TRUNCATE TABLE " + Qualified(name), ct);
        await BulkCopyAsync(cs, name, t, keys.Distinct().ToArray(), ct);
    }

    private async Task EnsureAsync(string cs, string name, TableDefinition t, CancellationToken ct)
    {
        var columns = Columns(t);
        var metadata = _schema.Table(t.Schema, t.Name);
        var declarations = string.Join(
            ", ",
            columns.Select((column, i) => $"[k{i}] {metadata.Column(column).StoreType} NOT NULL")
        );
        var index = "UX_" + name;
        var keyList = string.Join(", ", columns.Select((_, i) => $"[k{i}]"));
        await ExecuteAsync(
            cs,
            $"IF OBJECT_ID(N'{Qualified(name)}',N'U') IS NULL BEGIN "
                + $"CREATE TABLE {Qualified(name)} ({declarations}, [__generation] int NOT NULL, [__included] bit NOT NULL DEFAULT 0); "
                + $"CREATE UNIQUE INDEX {SqlServerIdentifier.Quote(index)} ON {Qualified(name)} ({keyList}); "
                + "END",
            ct
        );
    }

    private async Task BulkCopyAsync(
        string cs,
        string name,
        TableDefinition t,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken ct
    )
    {
        var columns = Columns(t);
        var metadata = _schema.Table(t.Schema, t.Name);
        var data = new DataTable();
        foreach (var pair in columns.Select((column, i) => (column, i)))
            data.Columns.Add($"k{pair.i}", metadata.Column(pair.column).ClrType);
        data.Columns.Add("__generation", typeof(int));
        foreach (var key in keys)
        {
            var row = data.NewRow();
            foreach (var pair in columns.Select((column, i) => (column, i)))
                row[$"k{pair.i}"] = key.Components.Single(x => DatabaseNames.Equals(x.Column, pair.column)).Value!;
            row["__generation"] = 0;
            data.Rows.Add(row);
        }

        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, null)
        {
            DestinationTableName = Qualified(name),
            EnableStreaming = true,
        };
        foreach (DataColumn column in data.Columns)
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(data, ct);
    }

    private async Task<IReadOnlyCollection<StableKey>> InsertNewAsync(
        TableDefinition t,
        int generation,
        CancellationToken ct
    )
    {
        var columns = Columns(t);
        var names = string.Join(", ", columns.Select((_, i) => $"[k{i}]"));
        var join = string.Join(" AND ", columns.Select((_, i) => $"s.[k{i}]=i.[k{i}]"));
        var inserted = string.Join(", ", columns.Select((_, i) => $"INSERTED.[k{i}]"));
        var sql =
            $"INSERT {Qualified(SourceTableName(t))} ({names},[__generation]) "
            + $"OUTPUT {inserted} "
            + $"SELECT {names},@generation FROM {Qualified(InputTableName(t))} i "
            + $"WHERE NOT EXISTS (SELECT 1 FROM {Qualified(SourceTableName(t))} s WHERE {join})";
        await using var connection = new SqlConnection(_source);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@generation", generation);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<StableKey>();
        while (await reader.ReadAsync(ct))
            result.Add(new StableKey(columns.Select((column, i) => new KeyComponent(column, reader.GetValue(i)))));
        return result;
    }

    private static async Task ExecuteAsync(string cs, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static Task DropAsync(string cs, string name) =>
        ExecuteAsync(cs, "DROP TABLE IF EXISTS " + Qualified(name), CancellationToken.None);
}
