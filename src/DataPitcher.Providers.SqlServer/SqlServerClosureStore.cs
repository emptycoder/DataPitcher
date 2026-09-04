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
        _stages = new SqlServerStagingTables(source, target, sourceSchema, keys, planId, dropOnDispose, targetSchema);
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
        var target = TargetDefinition(table);
        var targetColumns = columns.Select(column => TargetColumn(target, column)).ToArray();
        var select = string.Join(
            ", ",
            columns
                .Select((_, i) => $"s.[k{i}]")
                .Concat(targetColumns.Select(column => "t." + SqlServerIdentifier.Quote(column)))
        );
        var join = string.Join(
            " AND ",
            targetColumns.Select((column, i) => $"s.[k{i}]=t.{SqlServerIdentifier.Quote(column)}")
        );
        var sql =
            $"/* DataPitcher.ProbeTarget */ SELECT {select} "
            + $"FROM {SqlServerStagingTables.Qualified(_stages.TargetTableName(table))} s "
            + $"LEFT JOIN {Qualified(target)} t ON {join}";
        var states = outgoingRelationships.ToDictionary(relationship => relationship, TargetState);
        var result = new Dictionary<StableKey, TargetProbe>();
        await using var connection = new SqlConnection(_stages.TargetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
        {
            var targetKey = rows.IsDBNull(columns.Count) ? null : ReadTargetKey(rows, columns);
            result[ReadKey(rows, table)] = new TargetProbe(targetKey is not null, states, targetKey);
        }
        return result;
    }

    public Task ResetAsync(CancellationToken cancellationToken) => _stages.ResetAsync(cancellationToken);

    public Task MarkIncludedAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    ) => _stages.MarkIncludedAsync(table, keys, cancellationToken);

    public async Task<ClosureExpansion> ExpandAsync(
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
        // A left join keeps rows whose foreign key points at nothing, so orphans are counted instead of vanishing.
        var sql =
            $"SELECT {select} "
            + $"FROM {SqlServerStagingTables.Qualified(_stages.InputTableName(relationship.FromTable))} s "
            + $"JOIN {Qualified(relationship.FromTable)} f ON {sourceJoin} "
            + $"LEFT JOIN {Qualified(relationship.ToTable)} t ON {relationshipJoin} "
            + $"WHERE {required}";
        await using var connection = new SqlConnection(_stages.SourceConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<StableKey>();
        var orphans = 0L;
        while (await rows.ReadAsync(cancellationToken))
            if (rows.IsDBNull(0))
                orphans++;
            else
                result.Add(ReadKey(rows, relationship.ToTable));
        return new ClosureExpansion(result, orphans);
    }

    public ValueTask DisposeAsync() => _stages.DisposeAsync();

    private IReadOnlyList<string> KeyColumns(TableDefinition table) => _keys[table].Constraint!.Columns;

    /// <summary>The target's own spelling of a source table's name, so the probe can be quoted correctly.</summary>
    /// <summary>The target's own spelling of a key column, matched without regard to case.</summary>
    private static string TargetColumn(TableDefinition target, string column) =>
        target.Columns.FirstOrDefault(candidate => DatabaseNames.Equals(candidate.Name, column))?.Name ?? column;

    private TableDefinition TargetDefinition(TableDefinition table) =>
        _target
            .Tables.FirstOrDefault(candidate =>
                DatabaseNames.Equals(candidate.Definition.Schema, table.Schema)
                && DatabaseNames.Equals(candidate.Definition.Name, table.Name)
            )
            ?.Definition
        ?? table;

    private TargetConstraintState TargetState(ClosureRelationship relationship)
    {
        if (relationship.ForeignKey is not { } sourceForeignKey)
            return new TargetConstraintState(relationship.Name, false, false, false);

        var targetForeignKey = _target.ForeignKeys.SingleOrDefault(foreignKey =>
            foreignKey.ChildTable == sourceForeignKey.ChildTable
            && foreignKey.ParentTable == sourceForeignKey.ParentTable
            && foreignKey.ChildColumns.SequenceEqual(sourceForeignKey.ChildColumns, DatabaseNames.Comparer)
            && foreignKey.ParentColumns.SequenceEqual(sourceForeignKey.ParentColumns, DatabaseNames.Comparer)
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

    /// <summary>The matched target row's key under the source column names, read after the staged key columns.</summary>
    private static StableKey ReadTargetKey(SqlDataReader rows, IReadOnlyList<string> columns) =>
        new(columns.Select((column, i) => new KeyComponent(column, rows.GetValue(columns.Count + i))));

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
        var metadata = _source.Table(table.Schema, table.Name);
        foreach (var key in keys)
        foreach (var column in columns)
        {
            var value = key.Components.Single(component => DatabaseNames.Equals(component.Column, column)).Value;
            if (value is null || value.GetType() != metadata.Column(column).ClrType)
                throw new InvalidOperationException("Stable-key CLR type does not match catalog metadata.");
        }
    }

    private static string Qualified(TableDefinition table) => SqlServerIdentifier.Qualified(table.Schema, table.Name);
}
