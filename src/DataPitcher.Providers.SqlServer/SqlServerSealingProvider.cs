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

        public bool SupportsStableKeyType(SchemaColumn column)
        {
            var baseType = column.StoreType.Split('(')[0].Trim();
            try
            {
                return SqlServerStableKeyCodec.Supports(SqlServerTransferSchemaReader.Map(baseType).Item2);
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        public IReadOnlyCollection<TableDefinition> SourceTables { get; } =
            sourceCatalog.Tables.Select(table => table.Definition).ToArray();
        public IReadOnlyCollection<ForeignKeyDefinition> SourceForeignKeys { get; } =
            sourceCatalog.ForeignKeys.ToArray();
        public IReadOnlyCollection<UnresolvedForeignKey> SourceUnresolvedForeignKeys { get; } =
            sourceCatalog.UnresolvedForeignKeys.ToArray();

        public async Task<IReadOnlyCollection<UniqueKeyCollision>> FindUniqueKeyCollisionsAsync(
            IReadOnlyCollection<TableDefinition> planned,
            IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
            IReadOnlyDictionary<TableDefinition, TableMapping> mappings,
            Guid planId,
            CancellationToken cancellationToken
        )
        {
            var collisions = new List<UniqueKeyCollision>();
            foreach (var table in planned)
                collisions.AddRange(
                    await SqlServerUniqueKeyCollisions.FindAsync(
                        source,
                        target,
                        planId,
                        mappings[table],
                        stableKeys[table].Constraint!.Columns,
                        cancellationToken
                    )
                );
            return collisions;
        }

        public async Task<IReadOnlyCollection<string>> VerificationBlockersAsync(
            IReadOnlyCollection<TableAddress> tables,
            CancellationToken cancellationToken
        )
        {
            var strictExact = new SqlServerStrictExact(target);
            var blockers = new List<string>();
            foreach (var table in tables)
                blockers.AddRange(await strictExact.BlockersAsync(table, cancellationToken));
            return blockers;
        }

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
            // Each self reference sealing chose is levelled through the sealed keys, whatever key it references.
            foreach (var relationship in selfRelationships)
                await SqlServerStagingTables.StampHierarchyAsync(
                    source,
                    planId,
                    relationship.FromTable,
                    stableKeys[relationship.FromTable].Constraint!.Columns,
                    relationship.FromColumns,
                    relationship.ToColumns,
                    cancellationToken
                );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
