using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using DataPitcher.Infrastructure.Time;
using DataPitcher.Infrastructure.Worker;
using Xunit;

namespace DataPitcher.UnitTests.Worker;

public sealed class WorkerContractsTests
{
    [Fact]
    public async Task Faults_WhenPointIsConfigured_ThrowsOnlyForThatPoint()
    {
        var faults = new ScriptedWorkerFaults(TransferFaultPoint.BeforeTargetCommit);
        await Assert.ThrowsAsync<SimulatedWorkerFaultException>(() => faults.HitAsync(TransferFaultPoint.BeforeTargetCommit, CancellationToken.None));
        await faults.HitAsync(TransferFaultPoint.BeforeTargetCommit, CancellationToken.None);
        Assert.Contains(TransferFaultPoint.PermanentFailure, Enum.GetValues<TransferFaultPoint>());
    }

    [Fact]
    public void TransferUnit_WhenAtomicComponent_IsPausableOnlyAfterItsCommit()
    {
        var unit = new TransferUnit(4, new StableKey([new KeyComponent("Id", 9)]), 3, TransferUnitKind.AtomicComponent);
        Assert.True(unit.CanPauseAfterCommit);
        Assert.Equal(4, unit.BatchSequence);
        Assert.Equal(3, unit.RowCount);
    }

    [Fact]
    public void TransferUnit_WhenBatch_IsPausableAfterItsCommit()
    {
        var unit = new TransferUnit(5, new StableKey([new KeyComponent("Id", 10)]), 4, TransferUnitKind.Batch);

        Assert.True(unit.CanPauseAfterCommit);
    }

    [Fact]
    public void TransferUnit_WhenKindIsUnknown_IsNotPausable()
    {
        var unit = new TransferUnit(6, new StableKey([new KeyComponent("Id", 11)]), 5, (TransferUnitKind)99);

        Assert.False(unit.CanPauseAfterCommit);
    }

    [Fact]
    public void TransferProgressContracts_WhenBatchIsCommitted_PreserveByteCounts()
    {
        var unit = new TransferUnit(6, new StableKey([new KeyComponent("Id", 12)]), 6, TransferUnitKind.Batch, BytesTransferred: 600);
        var checkpoint = new TargetCheckpoint(Guid.NewGuid(), Guid.NewGuid(), 6, unit.LastStableKey, 60, "seal", 3, BytesTransferred: 6000);

        Assert.Equal(600, unit.BytesTransferred);
        Assert.Equal(6000, checkpoint.BytesTransferred);
    }

    [Fact]
    public async Task ClockWorkerDelay_WhenDueHasPassed_CompletesFromTheInjectedClock()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        var delay = new ClockWorkerDelay(clock);

        var wait = delay.UntilAsync(clock.UtcNow, CancellationToken.None);

        Assert.True(wait.IsCompletedSuccessfully);
        await wait;
    }

    [Fact]
    public async Task ClockWorkerDelay_WhenDueIsFuture_StaysPendingUntilCancellation()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        var delay = new ClockWorkerDelay(clock);
        using var cancellation = new CancellationTokenSource();

        var wait = delay.UntilAsync(clock.UtcNow.AddDays(1), cancellation.Token);

        Assert.False(wait.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public void TransferRun_WhenSealed_PreservesItsIdentityAndResumeCapability()
    {
        var run = new TransferRun(Guid.NewGuid(), Guid.NewGuid(), "seal", true, Guid.NewGuid(), Guid.NewGuid(), TransferMode.DirectFast);

        Assert.NotEqual(Guid.Empty, run.JobId); Assert.NotEqual(Guid.Empty, run.RunId); Assert.Equal("seal", run.ManifestSealHash); Assert.True(run.SupportsDurableResume);
    }

    [Fact]
    public void TransferContracts_WhenPlanAndTableAreProvided_PreserveThem()
    {
        var table = new TableAddress("dbo", "Orders"); var row = new TransferRow([1], 1); var rows = new[] { row }; var planId = Guid.NewGuid();
        var run = new TransferRun(Guid.NewGuid(), Guid.NewGuid(), "seal", true, Guid.NewGuid(), Guid.NewGuid(), TransferMode.DirectFast, planId);
        var unit = new TransferUnit(1, new StableKey([new KeyComponent("Id", 1)]), 1, TransferUnitKind.Batch, 1, table, rows);
        var checkpoint = new TargetCheckpoint(Guid.NewGuid(), Guid.NewGuid(), 1, unit.LastStableKey, 1, "seal", 1, 1, table);

        Assert.Equal(planId, run.PlanId);
        Assert.Equal(table, unit.Table);
        Assert.Same(row, Assert.Single(unit.Rows!));
        Assert.Equal(table, checkpoint.LastTable);
    }

    [Fact]
    public void RecoverySnapshot_WhenCreated_PreservesTheTargetCheckpointAndMutationJournal()
    {
        var checkpoint = new TargetCheckpoint(Guid.NewGuid(), Guid.NewGuid(), 2, new StableKey([new KeyComponent("Id", 2)]), 20, "seal", 3);
        var mutation = new TargetMutation("Orders", "OrdersTrigger", TargetMutationKind.DisabledTrigger);
        var entry = new MutationJournalEntry(Guid.NewGuid(), mutation, MutationJournalState.PendingRepair, "repair required");
        var snapshot = new RecoverySnapshot(checkpoint, [entry]);

        Assert.Same(checkpoint, snapshot.Checkpoint); Assert.Single(snapshot.Mutations, item => item.EntryId == entry.EntryId); Assert.Equal(mutation, snapshot.Mutations[0].Mutation); Assert.Equal(MutationJournalState.PendingRepair, snapshot.Mutations[0].State); Assert.Equal("repair required", snapshot.Mutations[0].Detail);
    }

    [Fact]
    public void TransferAttemptException_WhenCreated_PreservesCommitDispositionAndCause()
    {
        var cause = new InvalidOperationException("target connection dropped");
        var exception = new TransferAttemptException(CommitDisposition.Unknown, cause);

        Assert.Equal(CommitDisposition.Unknown, exception.Disposition); Assert.Same(cause, exception.InnerException); Assert.Equal(cause.Message, exception.Message);
    }

    [Fact]
    public void TargetFenceLostException_WhenCreated_ExplainsTheFenceFailure()
    {
        var exception = new TargetFenceLostException();

        Assert.Equal("Target fence token is no longer current.", exception.Message);
    }

    [Fact]
    public void ManifestSealMismatchException_WhenCreated_ExplainsTheSealFailure()
    {
        var exception = new ManifestSealMismatchException();

        Assert.Equal("Target checkpoint manifest seal hash does not match the sealed transfer run.", exception.Message);
    }

    [Fact]
    public void NonResumableInterruptedException_WhenCreated_ExplainsTheResumeFailure()
    {
        var exception = new NonResumableInterruptedException();

        Assert.Equal("Interrupted run does not support durable resume.", exception.Message);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class ScriptedWorkerFaults(TransferFaultPoint point) : IWorkerFaults
    {
        private bool _pending = true;
        public Task HitAsync(TransferFaultPoint current, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pending && current == point) { _pending = false; throw new SimulatedWorkerFaultException(point); }
            return Task.CompletedTask;
        }
    }
}
