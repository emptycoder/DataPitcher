using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;

namespace DataPitcher.Core.Transfer;

public sealed record TransferRun(
    Guid JobId,
    Guid RunId,
    string ManifestSealHash,
    bool SupportsDurableResume,
    Guid SourceConnectionId,
    Guid TargetConnectionId,
    TransferMode TransferMode,
    Guid PlanId = default
);

public interface ITransferConnectionRevalidator
{
    Task RevalidateAsync(TransferRun run, CancellationToken cancellationToken);
}

public sealed record TargetCheckpoint(
    Guid JobId,
    Guid RunId,
    long BatchSequence,
    StableKey? LastStableKey,
    long RowCount,
    string ManifestSealHash,
    long FenceToken,
    long BytesTransferred = 0,
    TableAddress? LastTable = null,
    /// <summary>Rows the target already had and that were therefore skipped, cumulative for the run.</summary>
    long SkippedRows = 0,
    /// <summary>The phase the last committed unit belonged to; a resume continues in the same phase.</summary>
    TransferPhase Phase = TransferPhase.Rows
);

/// <summary>
/// A transfer writes every table's rows first, then fills in the deferred columns (nullable foreign keys that
/// close a cycle) once all the rows they point at exist in the target.
/// </summary>
public enum TransferPhase
{
    Rows,
    DeferredColumns,
}

public enum TransferUnitKind
{
    Batch,
    AtomicComponent,

    /// <summary>Stable keys plus the deferred column values to fill in on rows this run already wrote.</summary>
    DeferredColumns,
}

public sealed record TransferUnit(
    long BatchSequence,
    StableKey LastStableKey,
    long RowCount,
    TransferUnitKind Kind,
    long BytesTransferred = 0,
    TableAddress? Table = null,
    IReadOnlyList<TransferRow>? Rows = null
)
{
    public bool CanPauseAfterCommit =>
        Kind is TransferUnitKind.Batch or TransferUnitKind.AtomicComponent or TransferUnitKind.DeferredColumns;
}

public enum TargetMutationKind
{
    DisabledConstraint,
    UntrustedConstraint,
    DisabledTrigger,
}

public enum MutationJournalState
{
    PendingRepair,
    Repaired,
    Quarantined,
}

public sealed record TargetMutation(string Table, string ObjectName, TargetMutationKind Kind);

public sealed record MutationJournalEntry(
    Guid EntryId,
    TargetMutation Mutation,
    MutationJournalState State,
    string? Detail
);

public sealed record RecoverySnapshot(TargetCheckpoint Checkpoint, IReadOnlyList<MutationJournalEntry> Mutations);

public enum CommitDisposition
{
    NotCommitted,
    Unknown,
}

public sealed class TransferAttemptException(CommitDisposition disposition, Exception innerException)
    : Exception(innerException.Message, innerException)
{
    public CommitDisposition Disposition { get; } = disposition;
}

/// <summary>Carries which unit the target was writing when it failed, so the failure detail names table and phase.</summary>
public sealed class TransferUnitException(TransferUnit unit, Exception innerException)
    : Exception(Describe(unit, innerException), innerException)
{
    public TransferUnit Unit { get; } = unit;

    private static string Describe(TransferUnit unit, Exception innerException)
    {
        var table = unit.Table is null ? "the target" : unit.Table.Schema + "." + unit.Table.Name;
        var what = unit.Kind == TransferUnitKind.DeferredColumns ? "deferred columns" : "rows";
        return $"Writing {what} of {table} (batch {unit.BatchSequence}) failed: {innerException.GetBaseException().Message}";
    }
}

public sealed class TargetFenceLostException : InvalidOperationException
{
    public TargetFenceLostException()
        : base("Target fence token is no longer current.") { }
}

public sealed class ManifestSealMismatchException : InvalidOperationException
{
    public ManifestSealMismatchException()
        : base("Target checkpoint manifest seal hash does not match the sealed transfer run.") { }
}

public sealed class NonResumableInterruptedException : InvalidOperationException
{
    public NonResumableInterruptedException()
        : base("Interrupted run does not support durable resume.") { }
}

public sealed class SimulatedWorkerFaultException(TransferFaultPoint point)
    : Exception($"Simulated worker fault: {point}.")
{
    public TransferFaultPoint Point { get; } = point;
}

public enum TransferFaultPoint
{
    BeforeTargetCommit,
    DuringTargetWrite,
    AfterTargetCommitBeforeControlMirror,
    ProcessInterrupted,
    TargetConnectionLost,
    Cancellation,
    CommandTimeout,
    TransientThenSuccess,
    PermanentFailure,
    RecoveryFailure,
}

public interface IWorkerFaults
{
    Task HitAsync(TransferFaultPoint point, CancellationToken cancellationToken);
}

public interface IJobRunCatalog
{
    Task<TransferRun> LoadAsync(TransferJob job, CancellationToken cancellationToken);
}

public interface ITransferReadSession : IAsyncDisposable
{
    Task<TransferUnit?> ReadNextAsync(CancellationToken cancellationToken);
    Task DiscardUncommittedAsync(CancellationToken cancellationToken);
}

public interface ITransferReadSessionFactory
{
    Task<ITransferReadSession> OpenKeysetAsync(
        TransferRun run,
        StableKey? startAfter,
        CancellationToken cancellationToken,
        TableAddress? table = null,
        TransferPhase phase = TransferPhase.Rows
    );
}

public interface ITargetRunSession : IAsyncDisposable
{
    Task<RecoverySnapshot> AcquireFenceReadCheckpointAndJournalAsync(
        TransferRun run,
        LeaseGrant lease,
        CancellationToken cancellationToken
    );
    Task<IReadOnlyList<MutationJournalEntry>> RepairMutationsAsync(
        IReadOnlyList<MutationJournalEntry> mutations,
        CancellationToken cancellationToken
    );
    Task QuarantineAsync(TargetMutation mutation, string reason, CancellationToken cancellationToken);
    Task<TargetCheckpoint> ApplyAsync(
        TransferRun run,
        LeaseGrant lease,
        TransferUnit unit,
        CancellationToken cancellationToken
    );
    Task DiscardUncommittedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Proves the completed run after the final batch committed: every planned row reached the target, nothing
    /// outside the plan was written, and the rows this run wrote resolve their foreign keys. Throws
    /// <see cref="TransferVerificationException"/> with the reason when it does not hold.
    /// </summary>
    Task VerifyAsync(TransferRun run, LeaseGrant lease, CancellationToken cancellationToken);
}

/// <summary>The completed transfer does not match its plan; the detail names what differs.</summary>
public sealed class TransferVerificationException(string detail) : InvalidOperationException(detail);

public interface ITargetRunSessionFactory
{
    Task<ITargetRunSession> OpenAsync(TransferRun run, CancellationToken cancellationToken);
}

public interface IControlCheckpointMirror
{
    Task OverwriteAsync(TargetCheckpoint checkpoint, CancellationToken cancellationToken);
}

/// <summary>A database provider's implementation of the transfer read and target sessions the worker drives.</summary>
public interface IRunSessionProvider : ITransferReadSessionFactory, ITargetRunSessionFactory
{
    string ProviderId { get; }
}
