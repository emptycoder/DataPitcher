using DataPitcher.Core.Closure;
using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Graph;

public enum RowReferenceState { NullParent, Planned, TargetSatisfied, Missing }

public sealed record RowReference(RowAddress Child, ForeignKeyDefinition Relationship, RowAddress? Parent, RowReferenceState State);

public sealed record MissingReference(RowAddress Child, ForeignKeyDefinition Relationship, RowAddress Parent);

public sealed record RowGraphLimits(int MaximumRecursionLevels, TimeSpan CommandTimeout, long MaximumTemporaryBytes);

public sealed class RowGraphRequest
{
    public RowGraphRequest(IEnumerable<RowAddress> plannedRows, IEnumerable<RowReference> references, RowGraphLimits limits)
    {
        PlannedRows = Array.AsReadOnly(plannedRows.ToArray());
        References = Array.AsReadOnly(references.ToArray());
        Limits = limits;
    }

    public IReadOnlyList<RowAddress> PlannedRows { get; }
    public IReadOnlyList<RowReference> References { get; }
    public RowGraphLimits Limits { get; }
}

public sealed class RowGraphAnalysis
{
    public RowGraphAnalysis(IEnumerable<RowAddress> insertionOrder, IEnumerable<RowAddress> unreachedRows, IEnumerable<MissingReference> missingReferences)
    {
        InsertionOrder = Array.AsReadOnly(insertionOrder.ToArray());
        UnreachedRows = Array.AsReadOnly(unreachedRows.ToArray());
        MissingReferences = Array.AsReadOnly(missingReferences.ToArray());
    }

    public IReadOnlyList<RowAddress> InsertionOrder { get; }
    public IReadOnlyList<RowAddress> UnreachedRows { get; }
    public IReadOnlyList<MissingReference> MissingReferences { get; }
    public bool IsAcyclic => UnreachedRows.Count == 0;
}

public interface ISetBasedRowGraphAnalyzer
{
    Task<RowGraphAnalysis> AnalyzeAsync(RowGraphRequest request, IReadOnlyCollection<ForeignKeyDefinition> excludedRelationships, CancellationToken cancellationToken);
}
