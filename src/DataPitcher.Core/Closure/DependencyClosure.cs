using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Closure;

public sealed class DependencyClosure(IClosureStore store)
{
    private sealed record Frontier(TableDefinition Table, StableKey Key, RootConflictPolicy? RootPolicy);

    public async Task<ClosureResult> ComputeAsync(ClosureRequest request, CancellationToken cancellationToken)
    {
        var participants = request
            .Roots.Select(root => root.Table)
            .Concat(
                request
                    .Relationships.Where(relationship => relationship.IsEnabled)
                    .SelectMany(relationship => new[] { relationship.FromTable, relationship.ToTable })
            )
            .Distinct();
        var blocked = participants.FirstOrDefault(table =>
            !request.StableKeySelections.TryGetValue(table, out var selection) || !HasUsableStableKey(table, selection)
        );
        if (blocked is not null)
            throw new BlockedTableException(blocked);

        var rootPolicies = new Dictionary<RowAddress, RootConflictPolicy>();
        foreach (var root in request.Roots)
        foreach (var key in root.Keys)
        {
            var address = new RowAddress(root.Table, key);
            if (rootPolicies.TryGetValue(address, out var policy) && policy != root.ConflictPolicy)
                throw new InvalidOperationException(
                    $"Conflicting root conflict policies for {root.Table.Schema}.{root.Table.Name}."
                );
            rootPolicies[address] = root.ConflictPolicy;
        }

        var frontier = new List<Frontier>();
        var included = new Dictionary<RowAddress, ClosureRow>();
        var warnings = new HashSet<TargetConstraintWarning>();

        foreach (var root in request.Roots)
        foreach (var key in await store.SeedRootKeysAsync(root.Table, root.Keys, cancellationToken))
            frontier.Add(new(root.Table, key, root.ConflictPolicy));

        for (var generation = 0; frontier.Count > 0; generation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expandable = new Dictionary<TableDefinition, List<StableKey>>();
            foreach (var group in frontier.GroupBy(item => item.Table))
            {
                var keys = group.Select(item => item.Key).Distinct().ToArray();
                var requirements = request
                    .Relationships.Where(relationship =>
                        relationship.IsEnabled && !relationship.IsInbound && relationship.FromTable == group.Key
                    )
                    .ToArray();
                var probes = await store.ProbeTargetAsync(group.Key, requirements, keys, cancellationToken);

                foreach (var item in group)
                {
                    var probe = probes[item.Key];
                    if (item.RootPolicy is null && probe.Exists)
                    {
                        foreach (var relationship in requirements)
                        {
                            if (!probe.Constraints.TryGetValue(relationship, out var state))
                                warnings.Add(new TargetConstraintWarning(relationship.Name));
                            else if (!state.IsPresent || !state.IsEnforced || !state.IsTrusted)
                                warnings.Add(new TargetConstraintWarning(state.ConstraintName));
                        }
                    }

                    var include = item.RootPolicy switch
                    {
                        RootConflictPolicy.FailOnConflict when probe.Exists => throw new RootConflictException(
                            new(item.Table, item.Key)
                        ),
                        RootConflictPolicy.SkipExisting => !probe.Exists,
                        RootConflictPolicy.Upsert => true,
                        null => !IsTargetSatisfied(probe, requirements),
                        _ => true,
                    };
                    if (!include)
                        continue;

                    included.TryAdd(new(item.Table, item.Key), new(item.Table, item.Key, generation));
                    if (!expandable.TryGetValue(item.Table, out var keysToExpand))
                        expandable[item.Table] = keysToExpand = [];
                    keysToExpand.Add(item.Key);
                }
            }

            var discovered = new HashSet<RowAddress>();
            foreach (var relationship in request.Relationships.Where(relationship => relationship.IsEnabled))
            {
                if (!expandable.TryGetValue(relationship.FromTable, out var fromKeys))
                    continue;

                foreach (
                    var key in await store.ExpandAsync(relationship, fromKeys.Distinct().ToArray(), cancellationToken)
                )
                    discovered.Add(new(relationship.ToTable, key));
            }

            frontier = [];
            foreach (var group in discovered.GroupBy(address => address.Table))
            {
                var keys = group.Select(address => address.Key).ToArray();
                foreach (var key in await store.InsertNewKeysAsync(group.Key, keys, generation + 1, cancellationToken))
                    frontier.Add(new(group.Key, key, null));
            }
        }

        return new ClosureResult(included.Values, warnings);
    }

    private static bool IsTargetSatisfied(TargetProbe probe, IReadOnlyCollection<ClosureRelationship> requirements) =>
        probe.Exists
        && requirements.All(relationship =>
            probe.Constraints.TryGetValue(relationship, out var state)
            && state is { IsPresent: true, IsEnforced: true, IsTrusted: true }
        );

    private static bool HasUsableStableKey(TableDefinition table, StableKeySelection selection)
    {
        if (selection.Constraint is not { Columns.Count: > 0 } constraint)
            return false;

        return table.PrimaryKey == constraint
            || constraint.Columns.All(name =>
                table.Columns.FirstOrDefault(column => StringComparer.Ordinal.Equals(column.Name, name))
                    is { IsNullable: false }
            );
    }
}

public sealed class RootConflictException(RowAddress row)
    : InvalidOperationException($"Root already exists: {row.Table.Schema}.{row.Table.Name}.");

public sealed class BlockedTableException(TableDefinition table)
    : InvalidOperationException($"Table has no valid stable key: {table.Schema}.{table.Name}.");
