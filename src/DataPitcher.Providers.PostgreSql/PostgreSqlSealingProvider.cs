using DataPitcher.Core.Closure;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlSealingProvider : ISealingProvider
{
    public string ProviderId => "postgresql";

    public async Task<ISealingSession> OpenAsync(
        ConnectionProfile source,
        string sourceConnectionString,
        ConnectionProfile target,
        string targetConnectionString,
        CancellationToken cancellationToken
    )
    {
        var introspector = new PostgreSqlSchemaIntrospector();
        var sourceSchema = await introspector.ReadAsync(source, sourceConnectionString, cancellationToken);
        var targetSchema = await introspector.ReadAsync(target, targetConnectionString, cancellationToken);
        var sourceData = NpgsqlDataSource.Create(sourceConnectionString);
        var targetData = NpgsqlDataSource.Create(targetConnectionString);
        try
        {
            var sourceCatalog = await new PostgreSqlCatalogReader(sourceData).ReadAsync(
                source.BusinessSchema,
                cancellationToken
            );
            var targetCatalog = await new PostgreSqlCatalogReader(targetData).ReadAsync(
                target.BusinessSchema,
                cancellationToken
            );
            return new Session(sourceData, targetData, sourceSchema, targetSchema, sourceCatalog, targetCatalog);
        }
        catch
        {
            await sourceData.DisposeAsync();
            await targetData.DisposeAsync();
            throw;
        }
    }

    private sealed class Session(
        NpgsqlDataSource source,
        NpgsqlDataSource target,
        SchemaSnapshotContent sourceSchema,
        SchemaSnapshotContent targetSchema,
        PostgreSqlSchemaSnapshot sourceCatalog,
        PostgreSqlSchemaSnapshot targetCatalog
    ) : ISealingSession
    {
        public SchemaSnapshotContent SourceSchema => sourceSchema;
        public SchemaSnapshotContent TargetSchema => targetSchema;
        public IReadOnlyCollection<TableDefinition> SourceTables { get; } =
            sourceCatalog.Tables.Select(table => table.Definition).ToArray();
        public IReadOnlyCollection<ForeignKeyDefinition> SourceForeignKeys { get; } =
            sourceCatalog.ForeignKeys.ToArray();
        public IReadOnlyCollection<UnresolvedForeignKey> SourceUnresolvedForeignKeys { get; } =
            sourceCatalog.UnresolvedForeignKeys.ToArray();

        public async Task<IReadOnlyCollection<string>> VerificationBlockersAsync(
            IReadOnlyCollection<TableAddress> tables,
            CancellationToken cancellationToken
        )
        {
            var strictExact = new PostgreSqlStrictExact(target);
            var blockers = new List<string>();
            foreach (var table in tables)
                blockers.AddRange(await strictExact.BlockersAsync(table, cancellationToken));
            return blockers;
        }

        public Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken) =>
            new PostgreSqlSelectionExecutor(source, sourceCatalog).ValidateAsync(selection, cancellationToken);

        public Task<SelectionKeySet> ReadKeysAsync(
            GeneratedSelectionSql selection,
            int maximumResultSize,
            CancellationToken cancellationToken
        ) =>
            new PostgreSqlSelectionExecutor(source, sourceCatalog).ReadKeysAsync(
                selection,
                maximumResultSize,
                cancellationToken
            );

        public IClosureStore CreateClosureStore(
            IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
            Guid planId
        ) => new PostgreSqlClosureStore(source, target, sourceCatalog, targetCatalog, stableKeys, planId, false);

        public async Task OrderHierarchiesAsync(
            IReadOnlyCollection<ClosureRelationship> selfRelationships,
            IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
            Guid planId,
            CancellationToken cancellationToken
        )
        {
            // Sealing passes only self references onto the stable key; those are levelled through the sealed keys.
            foreach (var relationship in selfRelationships)
                await PostgreSqlStagingTables.StampHierarchyAsync(
                    source,
                    planId,
                    relationship.FromTable,
                    stableKeys[relationship.FromTable].Constraint!.Columns,
                    relationship.FromColumns,
                    cancellationToken
                );
        }

        public async ValueTask DisposeAsync()
        {
            await source.DisposeAsync();
            await target.DisposeAsync();
        }
    }
}
