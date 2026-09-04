namespace DataPitcher.Core.Plans;

public sealed record PlanRecord(
    Guid PlanId,
    string DisplayName,
    string? OperatorNote,
    long Version,
    string? CanonicalHash,
    DateTimeOffset UpdatedUtc,
    Guid? SelectionId = null,
    Guid? SourceConnectionId = null,
    Guid? TargetConnectionId = null,
    /// <summary>Why the last sealing attempt failed, until the plan is sealed or edited again.</summary>
    string? SealFailureCode = null,
    string? SealFailureDetail = null,
    /// <summary>The operator's column-mapping choices; everything not listed here is mapped by name.</summary>
    IReadOnlyList<TableMappingOverride>? MappingOverrides = null
);

public sealed class PlanVersionMismatchException : InvalidOperationException
{
    public PlanVersionMismatchException()
        : base("Plan version does not match.") { }
}

public interface IPlanRepository
{
    Task<PlanRecord> SaveAsync(
        Guid planId,
        string displayName,
        string? operatorNote,
        string ifMatch,
        CancellationToken cancellationToken,
        Guid? selectionId = null,
        Guid? sourceConnectionId = null,
        Guid? targetConnectionId = null,
        IReadOnlyList<TableMappingOverride>? mappingOverrides = null
    );

    Task<PlanRecord?> FindAsync(Guid planId, CancellationToken cancellationToken);

    Task SealAsync(Guid planId, TransferPlanContent content, CancellationToken cancellationToken);

    Task<TransferPlanContent?> LoadContentAsync(Guid planId, CancellationToken cancellationToken);

    /// <summary>Remembers why sealing failed so the plan review can show it; cleared by the next seal or edit.</summary>
    Task RecordSealFailureAsync(Guid planId, string code, string detail, CancellationToken cancellationToken);
}
