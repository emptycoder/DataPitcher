using DataPitcher.Core.Closure;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerSealingProvider : ISealingProvider
{
    static SqlServerSealingProvider() => SqlServerEntraAuthentication.EnsureRegistered();

    public string ProviderId => "sqlserver";

    public async Task<ISealingSession> OpenAsync(
        ConnectionProfile source,
        string sourceConnectionString,
        ConnectionProfile target,
        string targetConnectionString,
        CancellationToken cancellationToken
    )
    {
        var introspector = new SqlServerSchemaIntrospector();
        var sourceSchema = await introspector.ReadAsync(source, sourceConnectionString, cancellationToken);
        var targetSchema = await introspector.ReadAsync(target, targetConnectionString, cancellationToken);
        var sourceCatalog = await new SqlServerCatalogReader(sourceConnectionString).ReadAsync(
            source.BusinessSchema,
            cancellationToken
        );
        var targetCatalog = await new SqlServerCatalogReader(targetConnectionString).ReadAsync(
            target.BusinessSchema,
            cancellationToken
        );
        return new Session(
            sourceConnectionString,
            targetConnectionString,
            sourceSchema,
            targetSchema,
            sourceCatalog,
            targetCatalog
        );
    }

    private sealed class Session(
        string source,
        string target,
        SchemaSnapshotContent sourceSchema,
        SchemaSnapshotContent targetSchema,
        SqlServerSchemaSnapshot sourceCatalog,
        SqlServerSchemaSnapshot targetCatalog
    ) : ISealingSession
    {
        public SchemaSnapshotContent SourceSchema => sourceSchema;
        public SchemaSnapshotContent TargetSchema => targetSchema;
        public IReadOnlyCollection<TableDefinition> SourceTables { get; } =
            sourceCatalog.Tables.Select(table => table.Definition).ToArray();
        public IReadOnlyCollection<ForeignKeyDefinition> SourceForeignKeys { get; } =
            sourceCatalog.ForeignKeys.ToArray();

        public Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken) =>
            new SqlServerSelectionExecutor(source, sourceCatalog).ValidateAsync(selection, cancellationToken);

        public Task<SelectionKeySet> ReadKeysAsync(
            GeneratedSelectionSql selection,
            int maximumResultSize,
            CancellationToken cancellationToken
        ) =>
            new SqlServerSelectionExecutor(source, sourceCatalog).ReadKeysAsync(
                selection,
                maximumResultSize,
                cancellationToken
            );

        public IClosureStore CreateClosureStore(
            IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
            Guid planId
        ) => new SqlServerClosureStore(source, target, sourceCatalog, targetCatalog, stableKeys, planId, false);

        public async Task OrderHierarchiesAsync(
            IReadOnlyCollection<ClosureRelationship> selfRelationships,
            IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
            Guid planId,
            CancellationToken cancellationToken
        )
        {
            foreach (var relationship in selfRelationships)
            {
                var keyColumns = stableKeys[relationship.FromTable].Constraint?.Columns;
                // Only a self reference onto the stable key can be levelled through the sealed keys.
                if (keyColumns is null || !relationship.ToColumns.SequenceEqual(keyColumns, StringComparer.Ordinal))
                    continue;
                await SqlServerStagingTables.StampHierarchyAsync(
                    source,
                    planId,
                    relationship.FromTable,
                    keyColumns,
                    relationship.FromColumns,
                    cancellationToken
                );
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
