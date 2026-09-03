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
    Guid? TargetConnectionId = null
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
        Guid? targetConnectionId = null
    );

    Task<PlanRecord?> FindAsync(Guid planId, CancellationToken cancellationToken);

    Task SealAsync(Guid planId, TransferPlanContent content, CancellationToken cancellationToken);

    Task<TransferPlanContent?> LoadContentAsync(Guid planId, CancellationToken cancellationToken);
}
