using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using Npgsql;
using NpgsqlTypes;

namespace DataPitcher.PostgreSql;

public sealed class PostgreSqlStagingTables : IAsyncDisposable
{
    private const string OwnerSchema = "datapitcher";

    private static readonly Dictionary<Type, (string Sql, NpgsqlDbType Import)> ColumnTypes = new()
    {
        [typeof(int)] = ("integer", NpgsqlDbType.Integer),
        [typeof(string)] = ("text", NpgsqlDbType.Text)
    };

    private readonly NpgsqlDataSource _source;
    private readonly PostgreSqlSchemaSnapshot _schema;
    private readonly IReadOnlyDictionary<TableDefinition, StableKeySelection> _stableKeys;
    private readonly string _plan = Guid.NewGuid().ToString("N");
    private readonly Dictionary<TableDefinition, int> _ordinals = [];
    private int _nextOrdinal;

    public PostgreSqlStagingTables(
        NpgsqlDataSource source,
        NpgsqlDataSource target,
        PostgreSqlSchemaSnapshot schema,
        IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys)
    {
        _source = source;
        _schema = schema;
        _stableKeys = stableKeys;
    }

    public string SourceTableName(TableDefinition table) => $"keys_{_plan}_{Ordinal(table):x8}";

    public async Task<IReadOnlyCollection<StableKey>> InsertSourceAsync(
        TableDefinition table, IReadOnlyCollection<StableKey> keys, int generation, CancellationToken ct)
    {
        var input = InputTableName(table);
        await EnsureTableAsync(SourceTableName(table), table, ct);
        await EnsureTableAsync(input, table, ct);
        await ExecuteAsync(_source, "TRUNCATE " + Qualified(input), ct);
        await CopyAsync(input, table, keys, generation, ct);
        return await InsertReturningAsync(table, generation, ct);
    }

    public async Task<int> GenerationAsync(TableDefinition table, StableKey key, CancellationToken ct)
    {
        var columns = KeyColumns(table);
        var predicate = string.Join(" AND ", columns.Select((_, i) => $"k{i} = @p{i}"));
        await using var command = _source.CreateCommand($"SELECT __generation FROM {Qualified(SourceTableName(table))} WHERE {predicate}");
        for (var i = 0; i < columns.Count; i++)
            command.Parameters.AddWithValue($"p{i}", key.Components.Single(x => x.Column == columns[i]).Value!);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var table in _ordinals.Keys)
        {
            await ExecuteAsync(_source, "DROP TABLE IF EXISTS " + Qualified(SourceTableName(table)), CancellationToken.None);
            await ExecuteAsync(_source, "DROP TABLE IF EXISTS " + Qualified(InputTableName(table)), CancellationToken.None);
        }
    }

    private string InputTableName(TableDefinition table) => $"input_{_plan}_{Ordinal(table):x8}";

    private int Ordinal(TableDefinition table) =>
        _ordinals.TryGetValue(table, out var value) ? value : _ordinals[table] = _nextOrdinal++;

    private IReadOnlyList<string> KeyColumns(TableDefinition table) => _stableKeys[table].Constraint!.Columns;

    private static string Qualified(string table) => PostgreSqlIdentifier.Qualified(OwnerSchema, table);

    private async Task EnsureTableAsync(string name, TableDefinition table, CancellationToken ct)
    {
        var columns = KeyColumns(table);
        var metadata = _schema.Table(table.Name);
        var declarations = string.Join(", ", columns.Select((column, i) => $"k{i} {ColumnTypes[metadata.Column(column).ClrType].Sql} NOT NULL"));
        var unique = string.Join(", ", columns.Select((_, i) => $"k{i}"));
        await ExecuteAsync(
            _source,
            $"CREATE SCHEMA IF NOT EXISTS {PostgreSqlIdentifier.Quote(OwnerSchema)}; " +
            $"CREATE TABLE IF NOT EXISTS {Qualified(name)} ({declarations}, __generation integer NOT NULL, UNIQUE ({unique}))",
            ct);
    }

    private async Task CopyAsync(string name, TableDefinition table, IReadOnlyCollection<StableKey> keys, int generation, CancellationToken ct)
    {
        var columns = KeyColumns(table);
        var metadata = _schema.Table(table.Name);
        var names = string.Join(", ", columns.Select((_, i) => $"k{i}").Append("__generation"));
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var importer = await connection.BeginBinaryImportAsync($"COPY {Qualified(name)} ({names}) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var key in keys)
        {
            await importer.StartRowAsync(ct);
            foreach (var column in columns)
                await importer.WriteAsync(key.Components.Single(x => x.Column == column).Value, ColumnTypes[metadata.Column(column).ClrType].Import, ct);
            await importer.WriteAsync(generation, NpgsqlDbType.Integer, ct);
        }

        await importer.CompleteAsync(ct);
    }

    private async Task<IReadOnlyCollection<StableKey>> InsertReturningAsync(TableDefinition table, int generation, CancellationToken ct)
    {
        var columns = KeyColumns(table);
        var names = string.Join(", ", columns.Select((_, i) => $"k{i}"));
        var sql = $"INSERT INTO {Qualified(SourceTableName(table))} ({names}, __generation) " +
                  $"SELECT {names}, @generation FROM {Qualified(InputTableName(table))} " +
                  $"ON CONFLICT ({names}) DO NOTHING RETURNING {names}";
        await using var command = _source.CreateCommand(sql);
        command.Parameters.AddWithValue("generation", generation);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<StableKey>();
        while (await reader.ReadAsync(ct))
            rows.Add(new StableKey(columns.Select((column, i) => new KeyComponent(column, reader.GetValue(i)))));
        return rows;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(ct);
    }
}
