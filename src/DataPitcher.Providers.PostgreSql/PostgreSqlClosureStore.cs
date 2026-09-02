using DataPitcher.Core.Closure;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlClosureStore : IClosureStore, IAsyncDisposable
{
    private readonly PostgreSqlStagingTables _stages;
    private readonly PostgreSqlSchemaSnapshot _target;
    private readonly IReadOnlyDictionary<TableDefinition, StableKeySelection> _stableKeys;

    public PostgreSqlClosureStore(
        NpgsqlDataSource source,
        NpgsqlDataSource target,
        PostgreSqlSchemaSnapshot sourceSchema,
        PostgreSqlSchemaSnapshot targetSchema,
        IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys)
    {
        _stages = new PostgreSqlStagingTables(source, target, sourceSchema, stableKeys);
        _target = targetSchema;
        _stableKeys = stableKeys;
    }

    public Task<IReadOnlyCollection<StableKey>> SeedRootKeysAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, CancellationToken cancellationToken) =>
        _stages.InsertSourceAsync(table, keys, 0, cancellationToken);

    public Task<IReadOnlyCollection<StableKey>> InsertNewKeysAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, int generation, CancellationToken cancellationToken) =>
        _stages.InsertSourceAsync(table, keys, generation, cancellationToken);

    public async Task<IReadOnlyDictionary<StableKey, TargetProbe>> ProbeTargetAsync(
        TableDefinition table, IReadOnlyCollection<ClosureRelationship> outgoingRelationships, IReadOnlyCollection<StableKey> keys, CancellationToken cancellationToken)
    {
        await _stages.ReplaceTargetCandidatesAsync(table, keys, cancellationToken);
        var states = outgoingRelationships.ToDictionary(relationship => relationship, TargetState);
        var columns = KeyColumns(table);
        var select = string.Join(", ", columns.Select((_, i) => $"s.k{i}"));
        var join = string.Join(" AND ", columns.Select((column, i) => $"s.k{i} = t.{PostgreSqlIdentifier.Quote(column)}"));
        var sql = $"/* DataPitcher.ProbeTarget */ SELECT {select}, t.{PostgreSqlIdentifier.Quote(columns[0])} IS NOT NULL " +
                  $"FROM {PostgreSqlStagingTables.Qualified(_stages.TargetTableName(table))} s " +
                  $"LEFT JOIN {Qualified(table)} t ON {join}";
        var result = new Dictionary<StableKey, TargetProbe>();
        await using var command = _stages.Target.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[ReadKey(reader, columns)] = new TargetProbe(reader.GetBoolean(columns.Count), states);
        return result;
    }

    public async Task<IReadOnlyCollection<StableKey>> ExpandAsync(ClosureRelationship relationship, IReadOnlyCollection<StableKey> fromKeys, CancellationToken cancellationToken)
    {
        await _stages.ReplaceSourceCandidatesAsync(relationship.FromTable, fromKeys, cancellationToken);
        var fromColumns = relationship.FromColumns;
        var toColumns = relationship.ToColumns;
        var fromKeyColumns = KeyColumns(relationship.FromTable);
        var toKeyColumns = KeyColumns(relationship.ToTable);
        var select = string.Join(", ", toKeyColumns.Select(column => $"t.{PostgreSqlIdentifier.Quote(column)}"));
        var sourceJoin = string.Join(" AND ", fromKeyColumns.Select((column, i) => $"s.k{i} = f.{PostgreSqlIdentifier.Quote(column)}"));
        var relationshipJoin = string.Join(" AND ", fromColumns.Zip(toColumns).Select(pair => $"f.{PostgreSqlIdentifier.Quote(pair.First)} = t.{PostgreSqlIdentifier.Quote(pair.Second)}"));
        var required = string.Join(" AND ", fromColumns.Select(column => $"f.{PostgreSqlIdentifier.Quote(column)} IS NOT NULL"));
        var sql = $"SELECT DISTINCT {select} " +
                  $"FROM {PostgreSqlStagingTables.Qualified(_stages.InputTableName(relationship.FromTable))} s " +
                  $"JOIN {Qualified(relationship.FromTable)} f ON {sourceJoin} " +
                  $"JOIN {Qualified(relationship.ToTable)} t ON {relationshipJoin} " +
                  $"WHERE {required}";
        await using var command = _stages.Source.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<StableKey>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadKey(reader, toKeyColumns));
        return result;
    }

    public ValueTask DisposeAsync() => _stages.DisposeAsync();

    private TargetConstraintState TargetState(ClosureRelationship relationship)
    {
        if (relationship.ForeignKey is not { } sourceFk)
            return new TargetConstraintState(relationship.Name, false, false, false);

        var fk = _target.ForeignKeys.SingleOrDefault(x =>
            x.ChildTable == sourceFk.ChildTable && x.ParentTable == sourceFk.ParentTable &&
            x.ChildColumns.SequenceEqual(sourceFk.ChildColumns) && x.ParentColumns.SequenceEqual(sourceFk.ParentColumns));
        return fk is null
            ? new TargetConstraintState(relationship.Name, false, false, false)
            : new TargetConstraintState(fk.Name, true, fk.IsEnforced, fk.IsTrusted);
    }

    private IReadOnlyList<string> KeyColumns(TableDefinition table) => _stableKeys[table].Constraint!.Columns;

    private static StableKey ReadKey(NpgsqlDataReader reader, IReadOnlyList<string> columns) =>
        new(columns.Select((column, i) => new KeyComponent(column, reader.GetValue(i))));

    private static string Qualified(TableDefinition table) => PostgreSqlIdentifier.Qualified(table.Schema, table.Name);
}
