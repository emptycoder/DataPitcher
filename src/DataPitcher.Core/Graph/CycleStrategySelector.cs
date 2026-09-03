using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Graph;

public enum CycleStrategy
{
    Ordered,
    Deferred,
    NullableTwoPhase,
    ConstraintSuspension,
    Blocked,
}

public sealed record CycleEdgeCapability(
    ForeignKeyDefinition Relationship,
    bool CanDeferCycleBreakingFk,
    bool CanUseNullableFkTwoPhase,
    bool CanSafelySuspendFk
);

public sealed class CycleStrategyCapabilities
{
    public CycleStrategyCapabilities(
        bool supportsDeferrableForeignKeys,
        IEnumerable<CycleEdgeCapability> edgeCapabilities
    )
    {
        SupportsDeferrableForeignKeys = supportsDeferrableForeignKeys;
        EdgeCapabilities = Array.AsReadOnly(edgeCapabilities.ToArray());
    }

    public bool SupportsDeferrableForeignKeys { get; }
    public IReadOnlyList<CycleEdgeCapability> EdgeCapabilities { get; }
    public bool CanDeferCycleBreakingFks =>
        SupportsDeferrableForeignKeys && EdgeCapabilities.Any(x => x.CanDeferCycleBreakingFk);
    public bool CanUseNullableFkTwoPhase => EdgeCapabilities.Any(x => x.CanUseNullableFkTwoPhase);
    public bool CanSafelySuspendFks => EdgeCapabilities.Any(x => x.CanSafelySuspendFk);
}

public sealed class CycleStrategySelection
{
    public CycleStrategySelection(
        CycleStrategy strategy,
        IEnumerable<ForeignKeyDefinition> cycleBreakingEdges,
        RowGraphAnalysis analysis,
        string explanation
    )
    {
        Strategy = strategy;
        CycleBreakingEdges = Array.AsReadOnly(cycleBreakingEdges.ToArray());
        Analysis = analysis;
        Explanation = explanation;
    }

    public CycleStrategy Strategy { get; }
    public IReadOnlyList<ForeignKeyDefinition> CycleBreakingEdges { get; }
    public RowGraphAnalysis Analysis { get; }
    public string Explanation { get; }
    public bool MustBlockScc => Strategy == CycleStrategy.Blocked;
}

public sealed class CycleStrategySelector(ISetBasedRowGraphAnalyzer analyzer)
{
    public async Task<CycleStrategySelection> SelectAsync(
        RowGraphRequest request,
        CycleStrategyCapabilities capabilities,
        CancellationToken cancellationToken
    )
    {
        var initial = await analyzer.AnalyzeAsync(request, [], cancellationToken);
        if (initial.IsAcyclic)
            return new(CycleStrategy.Ordered, [], initial, "The planned row graph is acyclic.");

        foreach (var candidate in Candidates(capabilities))
        {
            var residual = await analyzer.AnalyzeAsync(request, candidate.Edges, cancellationToken);
            if (residual.IsAcyclic)
                return new(
                    candidate.Strategy,
                    candidate.Edges,
                    residual,
                    $"{candidate.Strategy} breaks every planned row cycle."
                );
        }

        return new(CycleStrategy.Blocked, [], initial, BlockedExplanation(request));
    }

    private static IEnumerable<(CycleStrategy Strategy, IReadOnlyList<ForeignKeyDefinition> Edges)> Candidates(
        CycleStrategyCapabilities capabilities
    )
    {
        if (capabilities.CanDeferCycleBreakingFks)
            yield return (CycleStrategy.Deferred, Eligible(capabilities, x => x.CanDeferCycleBreakingFk));
        if (capabilities.CanUseNullableFkTwoPhase)
            yield return (CycleStrategy.NullableTwoPhase, Eligible(capabilities, x => x.CanUseNullableFkTwoPhase));
        if (capabilities.CanSafelySuspendFks)
            yield return (CycleStrategy.ConstraintSuspension, Eligible(capabilities, x => x.CanSafelySuspendFk));
    }

    private static IReadOnlyList<ForeignKeyDefinition> Eligible(
        CycleStrategyCapabilities capabilities,
        Func<CycleEdgeCapability, bool> eligible
    ) =>
        Array.AsReadOnly(
            capabilities
                .EdgeCapabilities.Where(eligible)
                .Select(x => x.Relationship)
                .Distinct()
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToArray()
        );

    private static string BlockedExplanation(RowGraphRequest request)
    {
        var tables = request
            .PlannedRows.Select(x => x.Table)
            .Distinct()
            .OrderBy(x => $"{x.Schema}.{x.Name}", StringComparer.Ordinal)
            .Select(x => $"{x.Schema}.{x.Name}");
        var relationships = request
            .References.Select(x => x.Relationship)
            .Distinct()
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name);
        return $"No single eligible cycle-breaking edge set orders the component. Affected tables: {string.Join(", ", tables)}. Relationships: {string.Join(", ", relationships)}.";
    }
}
