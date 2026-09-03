using System.Data;
using System.Security.Cryptography;
using System.Text;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
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
            command.Parameters.AddWithValue($"@p{i}", key.Components.Single(k => k.Column == columns[i]).Value!);
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
        var metadata = _schema.Table(t.Name);
        var declarations = string.Join(
            ", ",
            columns.Select((column, i) => $"[k{i}] {metadata.Column(column).StoreType} NOT NULL")
        );
        var index = "UX_" + name;
        var keyList = string.Join(", ", columns.Select((_, i) => $"[k{i}]"));
        await ExecuteAsync(
            cs,
            $"IF OBJECT_ID(N'{Qualified(name)}',N'U') IS NULL BEGIN "
                + $"CREATE TABLE {Qualified(name)} ({declarations}, [__generation] int NOT NULL); "
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
        var metadata = _schema.Table(t.Name);
        var data = new DataTable();
        foreach (var pair in columns.Select((column, i) => (column, i)))
            data.Columns.Add($"k{pair.i}", metadata.Column(pair.column).ClrType);
        data.Columns.Add("__generation", typeof(int));
        foreach (var key in keys)
        {
            var row = data.NewRow();
            foreach (var pair in columns.Select((column, i) => (column, i)))
                row[$"k{pair.i}"] = key.Components.Single(x => x.Column == pair.column).Value!;
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
