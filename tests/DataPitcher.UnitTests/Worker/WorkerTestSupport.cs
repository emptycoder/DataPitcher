using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Worker;

namespace DataPitcher.UnitTests.Worker;

internal sealed class GateWorkerDelay : IWorkerDelay
{
    private readonly TaskCompletionSource<DateTimeOffset> _firstDue = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _returnedToWait = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;
    public Task<DateTimeOffset> FirstDue => _firstDue.Task;
    public Task ReturnedToWait => _returnedToWait.Task;

    public void Release() => _release.TrySetResult(true);

    public async Task UntilAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            _firstDue.TrySetResult(dueUtc);
            await _release.Task.WaitAsync(cancellationToken);
            return;
        }
        _returnedToWait.TrySetResult(true);
        await _stopped.Task.WaitAsync(cancellationToken);
    }
}

internal sealed class BlockingWorkerDelay : IWorkerDelay
{
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task UntilAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken) =>
        _stopped.Task.WaitAsync(cancellationToken);
}

internal sealed class UnreachableWorkerDelay : IWorkerDelay
{
    public Task UntilAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class NeverClaimsJobControl : IJobControl
{
    public Task<JobClaim?> TryClaimNextAsync(string ownerId, TimeSpan leaseTtl, CancellationToken cancellationToken) =>
        Task.FromResult<JobClaim?>(null);

    public Task<JobState> GetStateAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromException<JobState>(new NotSupportedException());

    public Task RequestPauseAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task RequestResumeAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task RequestCancelAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task PrepareAsync(JobClaim claim, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task MarkRunningAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task MarkPausedAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task MarkCancelledAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task MarkVerifyingAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task MarkSucceededAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task MarkFailedAsync(LeaseGrant lease, string failureCode, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());
}

internal sealed class SingleClaimJobControl(JobClaim claim, List<string> calls) : IJobControl
{
    private readonly TaskCompletionSource<bool> _verifying = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _available = 1;
    public Task MarkedVerifying => _verifying.Task;

    public Task<JobClaim?> TryClaimNextAsync(string ownerId, TimeSpan leaseTtl, CancellationToken cancellationToken) =>
        Interlocked.Exchange(ref _available, 0) == 1
            ? Task.FromResult<JobClaim?>(claim)
            : WaitForStopAsync(cancellationToken);

    public Task<JobState> GetStateAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(JobState.Running);

    public Task PrepareAsync(JobClaim jobClaim, CancellationToken cancellationToken)
    {
        calls.Add("Prepare");
        return Task.CompletedTask;
    }

    public Task MarkRunningAsync(LeaseGrant lease, CancellationToken cancellationToken)
    {
        calls.Add("Running");
        return Task.CompletedTask;
    }

    public Task MarkVerifyingAsync(LeaseGrant lease, CancellationToken cancellationToken)
    {
        calls.Add("Verifying");
        _verifying.TrySetResult(true);
        return Task.CompletedTask;
    }

    public Task MarkSucceededAsync(LeaseGrant lease, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RequestPauseAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task RequestResumeAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task RequestCancelAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task MarkPausedAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task MarkCancelledAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task MarkFailedAsync(LeaseGrant lease, string failureCode, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    private async Task<JobClaim?> WaitForStopAsync(CancellationToken cancellationToken)
    {
        await _stopped.Task.WaitAsync(cancellationToken);
        return null;
    }
}

internal sealed class TestJobRunCatalog(TransferRun run) : IJobRunCatalog
{
    public Task<TransferRun> LoadAsync(TransferJob job, CancellationToken cancellationToken) => Task.FromResult(run);
}

internal sealed class TestTransferReadSession(TransferUnit unit, string connectionOwnerId) : ITransferReadSession
{
    private TransferUnit? _unit = unit;
    public string ConnectionOwnerId { get; } = connectionOwnerId;
    public bool Disposed { get; private set; }

    public Task<TransferUnit?> ReadNextAsync(CancellationToken cancellationToken)
    {
        var result = _unit;
        _unit = null;
        return Task.FromResult(result);
    }

    public Task DiscardUncommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestTransferReadSessionFactory(TestTransferReadSession session) : ITransferReadSessionFactory
{
    public StableKey? LastRequestedStartAfter { get; private set; }

    public Task<ITransferReadSession> OpenKeysetAsync(
        TransferRun run,
        StableKey? startAfter,
        CancellationToken cancellationToken,
        TableAddress? table = null
    )
    {
        LastRequestedStartAfter = startAfter;
        return Task.FromResult<ITransferReadSession>(session);
    }
}

internal sealed class TestTargetRunSession(TargetCheckpoint checkpoint, string connectionOwnerId, List<string> calls)
    : ITargetRunSession
{
    public string ConnectionOwnerId { get; } = connectionOwnerId;
    public bool Disposed { get; private set; }
    public RecoverySnapshot Snapshot { get; set; } = new(checkpoint, []);
    public IReadOnlyList<MutationJournalEntry> RepairResult { get; set; } = [];
    public List<TargetMutation> QuarantinedMutations { get; } = [];
    public LeaseGrant? LastFenceLease { get; private set; }

    public Task<TargetCheckpoint> ApplyAsync(
        TransferRun run,
        LeaseGrant lease,
        TransferUnit unit,
        CancellationToken cancellationToken
    )
    {
        calls.Add("Apply");
        return Task.FromResult(checkpoint);
    }

    public Task<RecoverySnapshot> AcquireFenceReadCheckpointAndJournalAsync(
        TransferRun run,
        LeaseGrant lease,
        CancellationToken cancellationToken
    )
    {
        calls.Add("Fence");
        LastFenceLease = lease;
        return Task.FromResult(Snapshot);
    }

    public Task<IReadOnlyList<MutationJournalEntry>> RepairMutationsAsync(
        IReadOnlyList<MutationJournalEntry> mutations,
        CancellationToken cancellationToken
    )
    {
        calls.Add("Repair");
        return Task.FromResult(RepairResult);
    }

    public Task QuarantineAsync(TargetMutation mutation, string reason, CancellationToken cancellationToken)
    {
        calls.Add("Quarantine");
        QuarantinedMutations.Add(mutation);
        return Task.CompletedTask;
    }

    public Task DiscardUncommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestTargetRunSessionFactory(TestTargetRunSession session) : ITargetRunSessionFactory
{
    public Task<ITargetRunSession> OpenAsync(TransferRun run, CancellationToken cancellationToken) =>
        Task.FromResult<ITargetRunSession>(session);
}

internal sealed class RecordingCheckpointMirror(List<string> calls) : IControlCheckpointMirror
{
    public List<TargetCheckpoint> Checkpoints { get; } = [];

    public Task OverwriteAsync(TargetCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        calls.Add("Mirror");
        Checkpoints.Add(checkpoint);
        return Task.CompletedTask;
    }
}

internal sealed class NoWorkerFaults : IWorkerFaults
{
    public Task HitAsync(TransferFaultPoint point, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class BoundaryJobControl(JobClaim claim) : IJobControl
{
    private readonly TaskCompletionSource<JobState> _terminalState = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _available = 1;
    public JobState State { get; set; } = JobState.Running;
    public Task<JobState> TerminalState => _terminalState.Task;
    public CancellationToken? CancelledToken { get; private set; }

    public Task<JobClaim?> TryClaimNextAsync(string ownerId, TimeSpan leaseTtl, CancellationToken cancellationToken) =>
        Interlocked.Exchange(ref _available, 0) == 1
            ? Task.FromResult<JobClaim?>(claim)
            : WaitForStopAsync(cancellationToken);

    public Task<JobState> GetStateAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(State);

    public Task PrepareAsync(JobClaim jobClaim, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task MarkRunningAsync(LeaseGrant lease, CancellationToken cancellationToken)
    {
        State = JobState.Running;
        return Task.CompletedTask;
    }

    public Task MarkPausedAsync(LeaseGrant lease, CancellationToken cancellationToken)
    {
        State = JobState.Paused;
        _terminalState.TrySetResult(State);
        return Task.CompletedTask;
    }

    public Task MarkCancelledAsync(LeaseGrant lease, CancellationToken cancellationToken)
    {
        CancelledToken = cancellationToken;
        State = JobState.Cancelled;
        _terminalState.TrySetResult(State);
        return Task.CompletedTask;
    }

    public Task MarkVerifyingAsync(LeaseGrant lease, CancellationToken cancellationToken)
    {
        State = JobState.Verifying;
        _terminalState.TrySetResult(State);
        return Task.CompletedTask;
    }

    public Task MarkSucceededAsync(LeaseGrant lease, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task MarkFailedAsync(LeaseGrant lease, string failureCode, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task RequestPauseAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task RequestResumeAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    public Task RequestCancelAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());

    private async Task<JobClaim?> WaitForStopAsync(CancellationToken cancellationToken)
    {
        await _stopped.Task.WaitAsync(cancellationToken);
        return null;
    }
}

internal sealed class PrefetchedReadSession(TransferUnit first, TransferUnit prefetched) : ITransferReadSession
{
    private TransferUnit? _next = first;
    private TransferUnit? _prefetched = prefetched;
    public bool Discarded { get; private set; }
    public bool Disposed { get; private set; }
    public bool HasPrefetchedUnit => _next is not null || _prefetched is not null;
    public CancellationToken? DiscardToken { get; private set; }

    public Task<TransferUnit?> ReadNextAsync(CancellationToken cancellationToken)
    {
        var next = _next;
        _next = _prefetched;
        _prefetched = null;
        return Task.FromResult(next);
    }

    public Task DiscardUncommittedAsync(CancellationToken cancellationToken)
    {
        Discarded = true;
        DiscardToken = cancellationToken;
        _next = null;
        _prefetched = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class PrefetchedReadSessionFactory(PrefetchedReadSession session) : ITransferReadSessionFactory
{
    public StableKey? LastRequestedStartAfter { get; private set; }

    public Task<ITransferReadSession> OpenKeysetAsync(
        TransferRun run,
        StableKey? startAfter,
        CancellationToken cancellationToken,
        TableAddress? table = null
    )
    {
        LastRequestedStartAfter = startAfter;
        return Task.FromResult<ITransferReadSession>(session);
    }
}

internal sealed class CommitBarrierTargetSession(TargetCheckpoint initialCheckpoint) : ITargetRunSession
{
    private readonly TaskCompletionSource<bool> _applyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _releaseCommit = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource<bool> _firstCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<long> DurableBatchSequences { get; } = [];
    public Task ApplyStarted => _applyStarted.Task;
    public Task FirstCommit => _firstCommit.Task;
    public CancellationToken? ApplyToken { get; private set; }
    public CancellationToken? DiscardToken { get; private set; }
    public bool Discarded { get; private set; }

    public void ReleaseCommit() => _releaseCommit.TrySetResult(true);

    public Task<RecoverySnapshot> AcquireFenceReadCheckpointAndJournalAsync(
        TransferRun run,
        LeaseGrant lease,
        CancellationToken cancellationToken
    ) => Task.FromResult(new RecoverySnapshot(initialCheckpoint, []));

    public Task<IReadOnlyList<MutationJournalEntry>> RepairMutationsAsync(
        IReadOnlyList<MutationJournalEntry> mutations,
        CancellationToken cancellationToken
    ) => Task.FromResult<IReadOnlyList<MutationJournalEntry>>([]);

    public Task QuarantineAsync(TargetMutation mutation, string reason, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task<TargetCheckpoint> ApplyAsync(
        TransferRun run,
        LeaseGrant lease,
        TransferUnit unit,
        CancellationToken cancellationToken
    )
    {
        ApplyToken = cancellationToken;
        if (DurableBatchSequences.Count == 0)
        {
            _applyStarted.TrySetResult(true);
            await _releaseCommit.Task.WaitAsync(cancellationToken);
        }
        DurableBatchSequences.Add(unit.BatchSequence);
        if (DurableBatchSequences.Count == 1)
            _firstCommit.TrySetResult(true);
        return new TargetCheckpoint(
            run.JobId,
            run.RunId,
            unit.BatchSequence,
            unit.LastStableKey,
            unit.RowCount,
            run.ManifestSealHash,
            lease.FenceToken
        );
    }

    public Task DiscardUncommittedAsync(CancellationToken cancellationToken)
    {
        Discarded = true;
        DiscardToken = cancellationToken;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class CommitBarrierTargetSessionFactory(CommitBarrierTargetSession session) : ITargetRunSessionFactory
{
    public Task<ITargetRunSession> OpenAsync(TransferRun run, CancellationToken cancellationToken) =>
        Task.FromResult<ITargetRunSession>(session);
}
