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
using Npgsql;
using NpgsqlTypes;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlStagingTables : IAsyncDisposable
{
    private const string OwnerSchema = "datapitcher";

    private static readonly Dictionary<Type, (string Sql, NpgsqlDbType Import)> ColumnTypes = new()
    {
        [typeof(int)] = ("integer", NpgsqlDbType.Integer),
        [typeof(string)] = ("text", NpgsqlDbType.Text),
    };

    private readonly NpgsqlDataSource _source;
    private readonly NpgsqlDataSource _target;
    private readonly PostgreSqlSchemaSnapshot _schema;
    private readonly IReadOnlyDictionary<TableDefinition, StableKeySelection> _stableKeys;
    private readonly string _plan;
    private readonly bool _dropOnDispose;
    private readonly HashSet<TableDefinition> _touched = [];

    public PostgreSqlStagingTables(
        NpgsqlDataSource source,
        NpgsqlDataSource target,
        PostgreSqlSchemaSnapshot schema,
        IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
        Guid? planId = null,
        bool dropOnDispose = true
    )
    {
        _source = source;
        _target = target;
        _schema = schema;
        _stableKeys = stableKeys;
        _plan = (planId ?? Guid.NewGuid()).ToString("D");
        _dropOnDispose = dropOnDispose;
    }

    public NpgsqlDataSource Source => _source;

    public NpgsqlDataSource Target => _target;

    public string SourceTableName(TableDefinition table) => Name("keys", table);

    /// <summary>The persisted root-key staging table the transfer worker joins against after sealing.</summary>
    public static string SourceTableName(Guid planId, TableAddress table) =>
        Name("keys", planId.ToString("D"), table.Schema, table.Name);

    public string InputTableName(TableDefinition table) => Name("input", table);

    public string TargetTableName(TableDefinition table) => Name("target", table);

    public async Task<IReadOnlyCollection<StableKey>> InsertSourceAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        int generation,
        CancellationToken ct
    )
    {
        var input = InputTableName(table);
        await EnsureTableAsync(_source, SourceTableName(table), table, ct);
        await EnsureTableAsync(_source, input, table, ct);
        await ExecuteAsync(_source, "TRUNCATE " + Qualified(input), ct);
        await CopyAsync(_source, input, table, keys, generation, ct);
        return await InsertReturningAsync(table, generation, ct);
    }

    public async Task ReplaceSourceCandidatesAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken ct
    )
    {
        var input = InputTableName(table);
        await EnsureTableAsync(_source, input, table, ct);
        await ExecuteAsync(_source, "TRUNCATE " + Qualified(input), ct);
        await CopyAsync(_source, input, table, keys, 0, ct);
    }

    public async Task ReplaceTargetCandidatesAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken ct
    )
    {
        var name = TargetTableName(table);
        await EnsureTableAsync(_target, name, table, ct);
        await ExecuteAsync(_target, "TRUNCATE " + Qualified(name), ct);
        await CopyAsync(_target, name, table, keys, 0, ct);
    }

    public async Task<int> GenerationAsync(TableDefinition table, StableKey key, CancellationToken ct)
    {
        var columns = KeyColumns(table);
        var predicate = string.Join(" AND ", columns.Select((_, i) => $"k{i} = @p{i}"));
        await using var command = _source.CreateCommand(
            $"SELECT __generation FROM {Qualified(SourceTableName(table))} WHERE {predicate}"
        );
        for (var i = 0; i < columns.Count; i++)
            command.Parameters.AddWithValue($"p{i}", key.Components.Single(x => x.Column == columns[i]).Value!);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_dropOnDispose)
            return;
        foreach (var table in _touched)
        {
            await ExecuteAsync(
                _source,
                "DROP TABLE IF EXISTS " + Qualified(SourceTableName(table)),
                CancellationToken.None
            );
            await ExecuteAsync(
                _source,
                "DROP TABLE IF EXISTS " + Qualified(InputTableName(table)),
                CancellationToken.None
            );
            await ExecuteAsync(
                _target,
                "DROP TABLE IF EXISTS " + Qualified(TargetTableName(table)),
                CancellationToken.None
            );
        }
    }

    private string Name(string prefix, TableDefinition table)
    {
        _touched.Add(table);
        return Name(prefix, _plan, table.Schema, table.Name);
    }

    // PostgreSQL identifiers are limited to 63 bytes, so the plan and table are folded into a short digest.
    private static string Name(string prefix, string plan, string schema, string table) =>
        prefix
        + "_"
        + Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan + "\u001f" + schema + "\u001f" + table)))
            .ToLowerInvariant()[..32];

    private IReadOnlyList<string> KeyColumns(TableDefinition table) => _stableKeys[table].Constraint!.Columns;

    public static string Qualified(string table) => PostgreSqlIdentifier.Qualified(OwnerSchema, table);

    private async Task EnsureTableAsync(
        NpgsqlDataSource dataSource,
        string name,
        TableDefinition table,
        CancellationToken ct
    )
    {
        var columns = KeyColumns(table);
        var metadata = _schema.Table(table.Name);
        var declarations = string.Join(
            ", ",
            columns.Select((column, i) => $"k{i} {ColumnTypes[metadata.Column(column).ClrType].Sql} NOT NULL")
        );
        var unique = string.Join(", ", columns.Select((_, i) => $"k{i}"));
        await ExecuteAsync(
            dataSource,
            $"CREATE SCHEMA IF NOT EXISTS {PostgreSqlIdentifier.Quote(OwnerSchema)}; "
                + $"CREATE TABLE IF NOT EXISTS {Qualified(name)} ({declarations}, __generation integer NOT NULL, UNIQUE ({unique}))",
            ct
        );
    }

    private async Task CopyAsync(
        NpgsqlDataSource dataSource,
        string name,
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        int generation,
        CancellationToken ct
    )
    {
        var columns = KeyColumns(table);
        var metadata = _schema.Table(table.Name);
        var names = string.Join(", ", columns.Select((_, i) => $"k{i}").Append("__generation"));
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var importer = await connection.BeginBinaryImportAsync(
            $"COPY {Qualified(name)} ({names}) FROM STDIN (FORMAT BINARY)",
            ct
        );
        foreach (var key in keys)
        {
            await importer.StartRowAsync(ct);
            foreach (var column in columns)
                await importer.WriteAsync(
                    key.Components.Single(x => x.Column == column).Value,
                    ColumnTypes[metadata.Column(column).ClrType].Import,
                    ct
                );
            await importer.WriteAsync(generation, NpgsqlDbType.Integer, ct);
        }

        await importer.CompleteAsync(ct);
    }

    private async Task<IReadOnlyCollection<StableKey>> InsertReturningAsync(
        TableDefinition table,
        int generation,
        CancellationToken ct
    )
    {
        var columns = KeyColumns(table);
        var names = string.Join(", ", columns.Select((_, i) => $"k{i}"));
        var sql =
            $"INSERT INTO {Qualified(SourceTableName(table))} ({names}, __generation) "
            + $"SELECT {names}, @generation FROM {Qualified(InputTableName(table))} "
            + $"ON CONFLICT ({names}) DO NOTHING RETURNING {names}";
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
