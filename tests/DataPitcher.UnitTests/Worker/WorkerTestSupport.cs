using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Worker;

namespace DataPitcher.UnitTests.Worker;

internal sealed class GateWorkerDelay : IWorkerDelay
{
    private readonly TaskCompletionSource<DateTimeOffset> _firstDue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _returnedToWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    public Task UntilAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken) => _stopped.Task.WaitAsync(cancellationToken);
}

internal sealed class UnreachableWorkerDelay : IWorkerDelay
{
    public Task UntilAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class NeverClaimsJobControl : IJobControl
{
    public Task<JobClaim?> TryClaimNextAsync(string ownerId, TimeSpan leaseTtl, CancellationToken cancellationToken) => Task.FromResult<JobClaim?>(null);
    public Task<JobState> GetStateAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromException<JobState>(new NotSupportedException());
    public Task RequestPauseAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task RequestResumeAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task RequestCancelAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task PrepareAsync(JobClaim claim, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task MarkRunningAsync(LeaseGrant lease, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task MarkPausedAsync(LeaseGrant lease, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task MarkCancelledAsync(LeaseGrant lease, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task MarkVerifyingAsync(LeaseGrant lease, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task MarkFailedAsync(LeaseGrant lease, string failureCode, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
}

internal sealed class SingleClaimJobControl(JobClaim claim, List<string> calls) : IJobControl
{
    private readonly TaskCompletionSource<bool> _verifying = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _available = 1;
    public Task MarkedVerifying => _verifying.Task;
    public Task<JobClaim?> TryClaimNextAsync(string ownerId, TimeSpan leaseTtl, CancellationToken cancellationToken) => Interlocked.Exchange(ref _available, 0) == 1 ? Task.FromResult<JobClaim?>(claim) : WaitForStopAsync(cancellationToken);
    public Task<JobState> GetStateAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(JobState.Running);
    public Task PrepareAsync(JobClaim jobClaim, CancellationToken cancellationToken) { calls.Add("Prepare"); return Task.CompletedTask; }
    public Task MarkRunningAsync(LeaseGrant lease, CancellationToken cancellationToken) { calls.Add("Running"); return Task.CompletedTask; }
    public Task MarkVerifyingAsync(LeaseGrant lease, CancellationToken cancellationToken) { calls.Add("Verifying"); _verifying.TrySetResult(true); return Task.CompletedTask; }
    public Task RequestPauseAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task RequestResumeAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task RequestCancelAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task MarkPausedAsync(LeaseGrant lease, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task MarkCancelledAsync(LeaseGrant lease, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task MarkFailedAsync(LeaseGrant lease, string failureCode, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    private async Task<JobClaim?> WaitForStopAsync(CancellationToken cancellationToken) { await _stopped.Task.WaitAsync(cancellationToken); return null; }
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
    public Task<TransferUnit?> ReadNextAsync(CancellationToken cancellationToken) { var result = _unit; _unit = null; return Task.FromResult(result); }
    public Task DiscardUncommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

internal sealed class TestTransferReadSessionFactory(TestTransferReadSession session) : ITransferReadSessionFactory
{
    public Task<ITransferReadSession> OpenKeysetAsync(TransferRun run, StableKey? startAfter, CancellationToken cancellationToken) => Task.FromResult<ITransferReadSession>(session);
}

internal sealed class TestTargetRunSession(TargetCheckpoint checkpoint, string connectionOwnerId, List<string> calls) : ITargetRunSession
{
    public string ConnectionOwnerId { get; } = connectionOwnerId;
    public bool Disposed { get; private set; }
    public Task<TargetCheckpoint> ApplyAsync(TransferRun run, LeaseGrant lease, TransferUnit unit, CancellationToken cancellationToken) { calls.Add("Apply"); return Task.FromResult(checkpoint); }
    public Task<RecoverySnapshot> AcquireFenceReadCheckpointAndJournalAsync(TransferRun run, LeaseGrant lease, CancellationToken cancellationToken) => Task.FromException<RecoverySnapshot>(new NotSupportedException());
    public Task<IReadOnlyList<MutationJournalEntry>> RepairMutationsAsync(IReadOnlyList<MutationJournalEntry> mutations, CancellationToken cancellationToken) => Task.FromException<IReadOnlyList<MutationJournalEntry>>(new NotSupportedException());
    public Task QuarantineAsync(TargetMutation mutation, string reason, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public Task DiscardUncommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

internal sealed class TestTargetRunSessionFactory(TestTargetRunSession session) : ITargetRunSessionFactory
{
    public Task<ITargetRunSession> OpenAsync(TransferRun run, CancellationToken cancellationToken) => Task.FromResult<ITargetRunSession>(session);
}

internal sealed class RecordingCheckpointMirror(List<string> calls) : IControlCheckpointMirror
{
    public List<TargetCheckpoint> Checkpoints { get; } = [];
    public Task OverwriteAsync(TargetCheckpoint checkpoint, CancellationToken cancellationToken) { calls.Add("Mirror"); Checkpoints.Add(checkpoint); return Task.CompletedTask; }
}

internal sealed class NoWorkerFaults : IWorkerFaults
{
    public Task HitAsync(TransferFaultPoint point, CancellationToken cancellationToken) => Task.CompletedTask;
}
