using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Closure;

public interface IClosureStore
{
    Task<IReadOnlyCollection<StableKey>> SeedRootKeysAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<StableKey, TargetProbe>> ProbeTargetAsync(TableDefinition table, IReadOnlyCollection<ClosureRelationship> outgoingRelationships, IReadOnlyCollection<StableKey> keys, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<StableKey>> ExpandAsync(ClosureRelationship relationship, IReadOnlyCollection<StableKey> fromKeys, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<StableKey>> InsertNewKeysAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, int generation, CancellationToken cancellationToken);
}
