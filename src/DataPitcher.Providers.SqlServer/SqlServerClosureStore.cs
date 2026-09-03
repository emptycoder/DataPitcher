using DataPitcher.Core.Closure;
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

public sealed class SqlServerClosureStore : IClosureStore, IAsyncDisposable
{
    private readonly SqlServerStagingTables _stages;
    private readonly SqlServerSchemaSnapshot _source;
    private readonly SqlServerSchemaSnapshot _target;
    private readonly IReadOnlyDictionary<TableDefinition, StableKeySelection> _keys;

    public SqlServerClosureStore(
        string source,
        string target,
        SqlServerSchemaSnapshot sourceSchema,
        SqlServerSchemaSnapshot targetSchema,
        IReadOnlyDictionary<TableDefinition, StableKeySelection> keys,
        Guid? planId = null,
        bool dropOnDispose = true
    )
    {
        _stages = new SqlServerStagingTables(source, target, sourceSchema, keys, planId, dropOnDispose);
        _source = sourceSchema;
        _target = targetSchema;
        _keys = keys;
    }

    public Task<IReadOnlyCollection<StableKey>> SeedRootKeysAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    )
    {
        ValidateKeyTypes(table, keys);
        return _stages.InsertSourceAsync(table, keys, 0, cancellationToken);
    }

    public Task<IReadOnlyCollection<StableKey>> InsertNewKeysAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        int generation,
        CancellationToken cancellationToken
    )
    {
        ValidateKeyTypes(table, keys);
        return _stages.InsertSourceAsync(table, keys, generation, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<StableKey, TargetProbe>> ProbeTargetAsync(
        TableDefinition table,
        IReadOnlyCollection<ClosureRelationship> outgoingRelationships,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    )
    {
        ValidateKeyTypes(table, keys);
        await _stages.ReplaceTargetCandidatesAsync(table, keys, cancellationToken);
        var columns = KeyColumns(table);
        var select = string.Join(", ", columns.Select((_, i) => $"s.[k{i}]"));
        var join = string.Join(
            " AND ",
            columns.Select((column, i) => $"s.[k{i}]=t.{SqlServerIdentifier.Quote(column)}")
        );
        var sql =
            $"/* DataPitcher.ProbeTarget */ SELECT {select}, CASE WHEN t.{SqlServerIdentifier.Quote(columns[0])} IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END "
            + $"FROM {SqlServerStagingTables.Qualified(_stages.TargetTableName(table))} s "
            + $"LEFT JOIN {Qualified(table)} t ON {join}";
        var states = outgoingRelationships.ToDictionary(relationship => relationship, TargetState);
        var result = new Dictionary<StableKey, TargetProbe>();
        await using var connection = new SqlConnection(_stages.TargetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
            result[ReadKey(rows, table)] = new TargetProbe(rows.GetBoolean(columns.Count), states);
        return result;
    }

    public async Task<IReadOnlyCollection<StableKey>> ExpandAsync(
        ClosureRelationship relationship,
        IReadOnlyCollection<StableKey> fromKeys,
        CancellationToken cancellationToken
    )
    {
        ValidateKeyTypes(relationship.FromTable, fromKeys);
        await _stages.ReplaceSourceCandidatesAsync(relationship.FromTable, fromKeys, cancellationToken);
        var fromKeyColumns = KeyColumns(relationship.FromTable);
        var toKeyColumns = KeyColumns(relationship.ToTable);
        var select = string.Join(", ", toKeyColumns.Select(column => $"t.{SqlServerIdentifier.Quote(column)}"));
        var sourceJoin = string.Join(
            " AND ",
            fromKeyColumns.Select((column, i) => $"s.[k{i}]=f.{SqlServerIdentifier.Quote(column)}")
        );
        var relationshipJoin = string.Join(
            " AND ",
            relationship
                .FromColumns.Zip(relationship.ToColumns)
                .Select(pair => $"f.{SqlServerIdentifier.Quote(pair.First)}=t.{SqlServerIdentifier.Quote(pair.Second)}")
        );
        var required = string.Join(
            " AND ",
            relationship.FromColumns.Select(column => $"f.{SqlServerIdentifier.Quote(column)} IS NOT NULL")
        );
        var sql =
            $"SELECT DISTINCT {select} "
            + $"FROM {SqlServerStagingTables.Qualified(_stages.InputTableName(relationship.FromTable))} s "
            + $"JOIN {Qualified(relationship.FromTable)} f ON {sourceJoin} "
            + $"JOIN {Qualified(relationship.ToTable)} t ON {relationshipJoin} "
            + $"WHERE {required}";
        await using var connection = new SqlConnection(_stages.SourceConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<StableKey>();
        while (await rows.ReadAsync(cancellationToken))
            result.Add(ReadKey(rows, relationship.ToTable));
        return result;
    }

    public ValueTask DisposeAsync() => _stages.DisposeAsync();

    private IReadOnlyList<string> KeyColumns(TableDefinition table) => _keys[table].Constraint!.Columns;

    private TargetConstraintState TargetState(ClosureRelationship relationship)
    {
        if (relationship.ForeignKey is not { } sourceForeignKey)
            return new TargetConstraintState(relationship.Name, false, false, false);

        var targetForeignKey = _target.ForeignKeys.SingleOrDefault(foreignKey =>
            foreignKey.ChildTable == sourceForeignKey.ChildTable
            && foreignKey.ParentTable == sourceForeignKey.ParentTable
            && foreignKey.ChildColumns.SequenceEqual(sourceForeignKey.ChildColumns)
            && foreignKey.ParentColumns.SequenceEqual(sourceForeignKey.ParentColumns)
        );
        return targetForeignKey is null
            ? new TargetConstraintState(relationship.Name, false, false, false)
            : new TargetConstraintState(
                targetForeignKey.Name,
                true,
                targetForeignKey.IsEnforced,
                targetForeignKey.IsTrusted
            );
    }

    private StableKey ReadKey(SqlDataReader rows, TableDefinition table)
    {
        var columns = KeyColumns(table);
        var components = columns.Select((column, i) => new KeyComponent(column, rows.GetValue(i))).ToArray();
        var key = new StableKey(components);
        ValidateKeyTypes(table, [key]);
        return key;
    }

    private void ValidateKeyTypes(TableDefinition table, IReadOnlyCollection<StableKey> keys)
    {
        var columns = KeyColumns(table);
        var metadata = _source.Table(table.Name);
        foreach (var key in keys)
        foreach (var column in columns)
        {
            var value = key.Components.Single(component => component.Column == column).Value;
            if (value is null || value.GetType() != metadata.Column(column).ClrType)
                throw new InvalidOperationException("Stable-key CLR type does not match catalog metadata.");
        }
    }

    private static string Qualified(TableDefinition table) => SqlServerIdentifier.Qualified(table.Schema, table.Name);
}
