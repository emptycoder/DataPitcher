using DataPitcher.Core.Closure;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;

namespace DataPitcher.Core.Plans;

/// <summary>
/// Everything plan sealing needs from a database provider: both schemas, the source catalog, selection execution
/// against the source, and a closure store spanning source and target. Sessions own their connections.
/// </summary>
public interface ISealingSession : IAsyncDisposable
{
    SchemaSnapshotContent SourceSchema { get; }
    SchemaSnapshotContent TargetSchema { get; }
    IReadOnlyCollection<TableDefinition> SourceTables { get; }
    IReadOnlyCollection<ForeignKeyDefinition> SourceForeignKeys { get; }

    /// <summary>Foreign keys of the source whose parent table the catalog could not load; see <see cref="UnresolvedForeignKey"/>.</summary>
    IReadOnlyCollection<UnresolvedForeignKey> SourceUnresolvedForeignKeys { get; }

    /// <summary>
    /// Why exact-set verification cannot be promised for these target tables (a trigger, a rule, a cascading
    /// path), one message per blocker. Empty when StrictExact is available.
    /// </summary>
    Task<IReadOnlyCollection<string>> VerificationBlockersAsync(
        IReadOnlyCollection<TableAddress> tables,
        CancellationToken cancellationToken
    );

    Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken);
    Task<SelectionKeySet> ReadKeysAsync(
        GeneratedSelectionSql selection,
        int maximumResultSize,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Orders the sealed keys of tables that reference themselves so that a row's parents in the same table are
    /// written before it: keys get a hierarchy level from the source's own parent links (through whichever key the
    /// reference targets), and the transfer pages through them ancestors first. Foreign keys therefore stay
    /// enforced on the target.
    /// </summary>
    Task OrderHierarchiesAsync(
        IReadOnlyCollection<ClosureRelationship> selfRelationships,
        IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
        Guid planId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds planned rows that collide with a different target row on a unique key other than the stable key, so
    /// sealing can refuse before a single batch is written.
    /// </summary>
    Task<IReadOnlyCollection<UniqueKeyCollision>> FindUniqueKeyCollisionsAsync(
        IReadOnlyCollection<TableDefinition> planned,
        IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
        Guid planId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates the closure store for this plan. Root key staging must survive the session so the transfer worker can
    /// read exactly the sealed rows later.
    /// </summary>
    IClosureStore CreateClosureStore(IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys, Guid planId);
}

public interface ISealingProvider
{
    string ProviderId { get; }
    Task<ISealingSession> OpenAsync(
        ConnectionProfile source,
        string sourceConnectionString,
        ConnectionProfile target,
        string targetConnectionString,
        CancellationToken cancellationToken
    );
}
