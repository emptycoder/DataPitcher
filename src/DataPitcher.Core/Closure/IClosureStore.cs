using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Closure;

public interface IClosureStore
{
    Task<IReadOnlyCollection<StableKey>> SeedRootKeysAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyDictionary<StableKey, TargetProbe>> ProbeTargetAsync(
        TableDefinition table,
        IReadOnlyCollection<ClosureRelationship> outgoingRelationships,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Follows one relationship from the given rows to their parents in the source. Rows whose foreign key is set
    /// but resolves to no parent are counted as orphans rather than silently dropped.
    /// </summary>
    Task<ClosureExpansion> ExpandAsync(
        ClosureRelationship relationship,
        IReadOnlyCollection<StableKey> fromKeys,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyCollection<StableKey>> InsertNewKeysAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        int generation,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Discards every key this plan staged before, so a plan sealed again starts from an empty set instead of
    /// treating its previous roots as already discovered.
    /// </summary>
    Task ResetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records that the closure decided to transfer these staged keys. Keys never marked stay staged for
    /// provenance but are not part of the transfer set.
    /// </summary>
    Task MarkIncludedAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    );
}
