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
    Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken);
    Task<SelectionKeySet> ReadKeysAsync(
        GeneratedSelectionSql selection,
        int maximumResultSize,
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
