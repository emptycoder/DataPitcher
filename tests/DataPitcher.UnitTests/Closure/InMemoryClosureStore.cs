using System.Collections.ObjectModel;
using DataPitcher.Core.Closure;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Closure;

public sealed class InMemoryClosureStore : IClosureStore
{
    private sealed record SourceRow(StableKey Key, IReadOnlyDictionary<string, object?> Values);

    private readonly HashSet<RowAddress> _target = [];
    private readonly Dictionary<RowAddress, int> _generations = [];
    private readonly Dictionary<(ClosureRelationship, StableKey), List<StableKey>> _links = [];
    private readonly Dictionary<(ClosureRelationship, StableKey), List<StableKey>> _reverseLinks = [];
    private readonly Dictionary<ClosureRelationship, TargetConstraintState> _targetConstraints = [];
    private readonly Dictionary<
        ClosureRelationship,
        (ClosureRelationship Relationship, TargetConstraintState State)
    > _probeConstraints = [];
    private readonly HashSet<ClosureRelationship> _omittedProbeConstraints = [];
    private readonly Dictionary<TableDefinition, List<SourceRow>> _sourceRows = [];
    public int SeedCalls { get; private set; }

    public void MarkTarget(TableDefinition table, params StableKey[] keys)
    {
        foreach (var key in keys)
            _target.Add(new(table, key));
    }

    public void Link(ClosureRelationship relationship, params (StableKey From, StableKey To)[] pairs)
    {
        foreach (var pair in pairs)
        {
            var key = (relationship, pair.From);
            if (!_links.TryGetValue(key, out var values))
                _links[key] = values = [];
            values.Add(pair.To);
        }
    }

    public void LinkReverse(ClosureRelationship relationship, params (StableKey To, StableKey From)[] pairs)
    {
        foreach (var pair in pairs)
        {
            var key = (relationship, pair.To);
            if (!_reverseLinks.TryGetValue(key, out var values))
                _reverseLinks[key] = values = [];
            values.Add(pair.From);
        }
    }

    public void SetTargetConstraint(ClosureRelationship relationship, TargetConstraintState state) =>
        _targetConstraints[relationship] = state;

    public void SetProbeConstraint(
        ClosureRelationship relationship,
        ClosureRelationship probeRelationship,
        TargetConstraintState state
    ) => _probeConstraints[relationship] = (probeRelationship, state);

    public void OmitProbeConstraint(ClosureRelationship relationship) => _omittedProbeConstraints.Add(relationship);

    public void AddRow(TableDefinition table, StableKey key, IReadOnlyDictionary<string, object?> values)
    {
        if (!_sourceRows.TryGetValue(table, out var rows))
            _sourceRows[table] = rows = [];
        rows.Add(
            new SourceRow(key, new ReadOnlyDictionary<string, object?>(values.ToDictionary(x => x.Key, x => x.Value)))
        );
    }

    public Task<IReadOnlyCollection<StableKey>> SeedRootKeysAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    )
    {
        SeedCalls++;
        return InsertAsync(table, keys, 0);
    }

    public Task<IReadOnlyDictionary<StableKey, TargetProbe>> ProbeTargetAsync(
        TableDefinition table,
        IReadOnlyCollection<ClosureRelationship> outgoingRelationships,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    )
    {
        var states = outgoingRelationships
            .Where(relationship => !_omittedProbeConstraints.Contains(relationship))
            .Select(relationship =>
                _probeConstraints.GetValueOrDefault(
                    relationship,
                    (
                        Relationship: relationship,
                        State: _targetConstraints.GetValueOrDefault(
                            relationship,
                            new TargetConstraintState(relationship.Name, false, false, false)
                        )
                    )
                )
            )
            .ToDictionary(entry => entry.Relationship, entry => entry.State);
        return Task.FromResult<IReadOnlyDictionary<StableKey, TargetProbe>>(
            keys.ToDictionary(key => key, key => new TargetProbe(_target.Contains(new(table, key)), states))
        );
    }

    public Task<ClosureExpansion> ExpandAsync(
        ClosureRelationship relationship,
        IReadOnlyCollection<StableKey> fromKeys,
        CancellationToken cancellationToken
    )
    {
        if (
            relationship.ForeignKey is { } foreignKey
            && !relationship.IsInbound
            && _sourceRows.ContainsKey(relationship.FromTable)
        )
        {
            var resolved = fromKeys.Select(key => Resolve(foreignKey, key).ToArray()).ToArray();
            return Task.FromResult(
                new ClosureExpansion(
                    resolved.SelectMany(keys => keys).Distinct().ToArray(),
                    fromKeys.Zip(resolved).Count(pair => pair.Second.Length == 0 && References(foreignKey, pair.First))
                )
            );
        }
        return Task.FromResult(
            new ClosureExpansion(
                fromKeys
                    .SelectMany(key =>
                        _links.GetValueOrDefault((relationship, key))
                        ?? _reverseLinks.GetValueOrDefault((relationship, key))
                        ?? []
                    )
                    .Distinct()
                    .ToArray(),
                _orphans.GetValueOrDefault(relationship)
            )
        );
    }

    private readonly Dictionary<ClosureRelationship, long> _orphans = [];
    private readonly Dictionary<TableDefinition, HashSet<StableKey>> _included = [];

    /// <summary>Declares that following the relationship from the linked rows leaves this many rows without a parent.</summary>
    public void SetOrphans(ClosureRelationship relationship, long rows) => _orphans[relationship] = rows;

    public IReadOnlyCollection<StableKey> Included(TableDefinition table) =>
        _included.TryGetValue(table, out var keys) ? keys : [];

    public int ResetCalls { get; private set; }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        ResetCalls++;
        _generations.Clear();
        _included.Clear();
        return Task.CompletedTask;
    }

    public Task MarkIncludedAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    )
    {
        if (!_included.TryGetValue(table, out var set))
            _included[table] = set = [];
        set.UnionWith(keys);
        return Task.CompletedTask;
    }

    /// <summary>Whether the child row carries a complete, non-null foreign-key value.</summary>
    private bool References(ForeignKeyDefinition foreignKey, StableKey childKey) =>
        _sourceRows.TryGetValue(foreignKey.ChildTable, out var children)
        && children.Any(row =>
            row.Key == childKey
            && foreignKey.ChildColumns.All(column => row.Values.GetValueOrDefault(column) is not null)
        );

    public Task<IReadOnlyCollection<StableKey>> InsertNewKeysAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        int generation,
        CancellationToken cancellationToken
    ) => InsertAsync(table, keys, generation);

    private IEnumerable<StableKey> Resolve(ForeignKeyDefinition foreignKey, StableKey childKey)
    {
        if (
            !_sourceRows.TryGetValue(foreignKey.ChildTable, out var children)
            || !_sourceRows.TryGetValue(foreignKey.ParentTable, out var parents)
        )
            yield break;
        foreach (var child in children.Where(row => row.Key == childKey))
        {
            var values = foreignKey.ChildColumns.Select(column => child.Values.GetValueOrDefault(column)).ToArray();
            if (values.Any(value => value is null))
                continue;
            foreach (
                var parent in parents.Where(row =>
                    foreignKey
                        .ParentColumns.Select(column => row.Values.GetValueOrDefault(column))
                        .SequenceEqual(values)
                )
            )
                yield return parent.Key;
        }
    }

    private Task<IReadOnlyCollection<StableKey>> InsertAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        int generation
    ) =>
        Task.FromResult<IReadOnlyCollection<StableKey>>(
            keys.Where(key => _generations.TryAdd(new(table, key), generation)).ToArray()
        );
}

public sealed class InMemoryClosureStoreTests
{
    [Fact]
    public async Task InMemoryClosureStore_WhenKeyWasAlreadyStaged_ReturnsOnlyGenuinelyNewKeys()
    {
        var table = new TableDefinition("dbo", "T", [], null, []);
        var key = new StableKey([new KeyComponent("K", 1)]);
        var store = new InMemoryClosureStore();
        Assert.Single(await store.InsertNewKeysAsync(table, [key], 1, CancellationToken.None));
        Assert.Empty(await store.InsertNewKeysAsync(table, [key], 2, CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryClosureStore_WhenTableIsRehydrated_UsesTheSameRowAddress()
    {
        var original = new TableDefinition("dbo", "T", [], null, []);
        var rehydrated = new TableDefinition("dbo", "T", [], null, []);
        var key = new StableKey([new KeyComponent("K", 1)]);
        var store = new InMemoryClosureStore();
        Assert.Single(await store.InsertNewKeysAsync(original, [key], 1, CancellationToken.None));
        Assert.Empty(await store.InsertNewKeysAsync(rehydrated, [key], 2, CancellationToken.None));
    }
}
