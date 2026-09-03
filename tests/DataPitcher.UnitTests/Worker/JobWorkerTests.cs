using DataPitcher.Application.Connections;
using DataPitcher.Application.Events;
using DataPitcher.Application.Worker;
using DataPitcher.ControlStore;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using DataPitcher.UnitTests.Infrastructure;
using Xunit;

namespace DataPitcher.UnitTests.Worker;

public sealed class JobWorkerTests
{
    [Fact]
    public async Task LeaseRenewer_WhenClockReachesRenewalDue_RenewsWithoutChangingTheFence()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var ttl = TimeSpan.FromMinutes(1);
        var leases = new LeaseStore(fixture.Database, fixture.Clock);
        var lease = leases.Acquire(jobId, "worker-a", ttl)!;
        var delay = new GateWorkerDelay();
        var renewer = new LeaseRenewer(leases, delay);
        using var leaseLost = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var renewing = renewer.RunAsync(lease, ttl, leaseLost, stop.Token);

        Assert.Equal(lease.RenewAfterUtc, await delay.FirstDue);
        fixture.Clock.Advance(lease.RenewAfterUtc - fixture.Clock.UtcNow);
        delay.Release();
        await delay.ReturnedToWait;

        using var db = fixture.Database.Open();
        Assert.Equal(
            lease.FenceToken,
            db.Query<long>(
                    "SELECT FenceToken FROM JobLeases WHERE JobId = @jobId",
                    new ControlParameter("jobId", jobId.ToString())
                )
                .Single()
        );
        Assert.Equal(
            fixture.Clock.UtcNow.Add(ttl).ToString("O"),
            db.Query<string>(
                    "SELECT ExpiresUtc FROM JobLeases WHERE JobId = @jobId",
                    new ControlParameter("jobId", jobId.ToString())
                )
                .Single()
        );
        stop.Cancel();
        await renewing;
        Assert.False(leaseLost.IsCancellationRequested);
    }

    [Fact]
    public async Task LeaseRenewer_WhenRenewalIsRejected_CancelsTheLeaseLostToken()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var ttl = TimeSpan.FromMinutes(1);
        var leases = new LeaseStore(fixture.Database, fixture.Clock);
        var lease = leases.Acquire(jobId, "worker-a", ttl)!;
        var delay = new GateWorkerDelay();
        var renewer = new LeaseRenewer(leases, delay);
        using var leaseLost = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var renewing = renewer.RunAsync(lease, ttl, leaseLost, stop.Token);

        Assert.Equal(lease.RenewAfterUtc, await delay.FirstDue);
        fixture.Clock.Advance(lease.ExpiresUtc - fixture.Clock.UtcNow + TimeSpan.FromTicks(1));
        delay.Release();
        await renewing;

        Assert.True(leaseLost.IsCancellationRequested);
    }

    [Fact]
    public async Task LeaseRenewer_WhenStopIsAlreadyRequested_ReturnsWithoutRenewing()
    {
        var lease = new LeaseGrant(
            Guid.NewGuid(),
            "worker-a",
            1,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            DateTimeOffset.UnixEpoch
        );
        var renewer = new LeaseRenewer(new LeaseStore(null!, null!), new UnreachableWorkerDelay());
        using var leaseLost = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await renewer.RunAsync(lease, TimeSpan.FromMinutes(1), leaseLost, stop.Token);

        Assert.False(leaseLost.IsCancellationRequested);
    }

    [Fact]
    public async Task JobWorker_WhenStartTokenIsAlreadyCancelled_NeverClaimsAJob()
    {
        var worker = new JobWorker(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            "worker-a",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1)
        );
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task JobWorker_WhenNoJobIsQueued_WaitsForTheNextPollInterval()
    {
        using var fixture = new ControlDatabaseFixture();
        var pollInterval = TimeSpan.FromMinutes(1);
        var delay = new GateWorkerDelay();
        var worker = new JobWorker(
            new NeverClaimsJobControl(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            delay,
            fixture.Clock,
            "worker-a",
            TimeSpan.FromMinutes(1),
            pollInterval
        );

        await worker.StartAsync(CancellationToken.None);
        var due = await delay.FirstDue;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(fixture.Clock.UtcNow.Add(pollInterval), due);
    }

    [Fact]
    public async Task JobWorker_WhenClaimRunsNormally_MirrorsTheTargetCheckpointAndDisposesItsSessions()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var runId = Guid.NewGuid();
        var ttl = TimeSpan.FromMinutes(1);
        var lease = new LeaseGrant(
            jobId,
            "worker-a",
            1,
            fixture.Clock.UtcNow.Add(ttl),
            fixture.Clock.UtcNow.AddMinutes(1)
        );
        var job = new TransferJob(jobId, runId, Guid.NewGuid(), "normal-run", JobState.Queued);
        var run = new TransferRun(jobId, runId, "seal", true, Guid.NewGuid(), Guid.NewGuid(), TransferMode.DirectFast);
        var unit = new TransferUnit(1, new StableKey([new KeyComponent("Id", 1)]), 1, TransferUnitKind.Batch);
        var checkpoint = new TargetCheckpoint(jobId, runId, 1, unit.LastStableKey, 1, "seal", lease.FenceToken);
        var recoveredKey = new StableKey([new KeyComponent("Id", 700)]);
        var recoveredCheckpoint = new TargetCheckpoint(jobId, runId, 7, recoveredKey, 700, "seal", lease.FenceToken);
        var calls = new List<string>();
        var jobs = new SingleClaimJobControl(new JobClaim(job, lease, false), calls);
        var source = new TestTransferReadSession(unit, "source-connection");
        var sourceFactory = new TestTransferReadSessionFactory(source);
        var target = new TestTargetRunSession(checkpoint, "target-connection", calls)
        {
            Snapshot = new(recoveredCheckpoint, []),
        };
        var mirror = new RecordingCheckpointMirror(calls);
        var worker = new JobWorker(
            jobs,
            new TestJobRunCatalog(run),
            new NoopConnectionRevalidator(),
            new TestTargetRunSessionFactory(target),
            sourceFactory,
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            new RecordingJobEventWriter(),
            new NoWorkerFaults(),
            new BlockingWorkerDelay(),
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await jobs.MarkedVerifying;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(
            ["Prepare", "Fence", "Repair", "Mirror", "Running", "Apply", "Mirror", "Verifying", "Verify"],
            calls
        );
        Assert.Equal(recoveredKey, sourceFactory.LastRequestedStartAfter);
        Assert.Contains(mirror.Checkpoints, item => item == recoveredCheckpoint);
        Assert.Contains(mirror.Checkpoints, item => item == checkpoint);
        Assert.True(source.Disposed);
        Assert.True(target.Disposed);
        Assert.NotEqual(source.ConnectionOwnerId, target.ConnectionOwnerId);
    }

    [Fact]
    public async Task JobWorker_WhenVerificationFindsTheTargetOffPlan_MarksTheJobVerificationFailed()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "verification-failed-run")).Job;
        var ttl = TimeSpan.FromMinutes(1);
        var run = new TransferRun(
            job.JobId,
            job.RunId,
            "seal",
            true,
            Guid.NewGuid(),
            Guid.NewGuid(),
            TransferMode.DirectFast
        );
        var unit = new TransferUnit(1, new StableKey([new KeyComponent("Id", 1)]), 1, TransferUnitKind.Batch);
        var checkpoint = new TargetCheckpoint(job.JobId, job.RunId, 0, null, 0, "seal", 1);
        var target = new TestTargetRunSession(checkpoint, "target", [])
        {
            VerificationFailure = "dbo.T: the plan sealed 2 row(s) but the run moved 1",
        };
        var mirror = new RecordingCheckpointMirror([]);
        var events = new RecordingJobEventWriter();
        var delay = new GateWorkerDelay();
        var worker = new JobWorker(
            store,
            new TestJobRunCatalog(run),
            new NoopConnectionRevalidator(),
            new TestTargetRunSessionFactory(target),
            new TestTransferReadSessionFactory(new TestTransferReadSession(unit, "source")),
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            events,
            new NoWorkerFaults(),
            delay,
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await delay.FirstDue;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(JobState.VerificationFailed, await store.GetStateAsync(job.JobId, CancellationToken.None));
        Assert.Equal("verification_failed", store.Get(job.JobId).FailureCode);
        Assert.Equal("dbo.T: the plan sealed 2 row(s) but the run moved 1", store.Get(job.JobId).FailureDetail);
        var announced = Assert.Single(events.Appends, item => item.Payload.State == "verification_failed");
        Assert.Equal("dbo.T: the plan sealed 2 row(s) but the run moved 1", announced.Payload.Detail);
    }

    [Fact]
    public async Task JobWorker_WhenThePlanWasSealedByAnOlderAlgorithm_FailsTheJobAsStale()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var ttl = TimeSpan.FromMinutes(1);
        var lease = new LeaseGrant(
            jobId,
            "worker-a",
            1,
            fixture.Clock.UtcNow.Add(ttl),
            fixture.Clock.UtcNow.AddMinutes(1)
        );
        var job = new TransferJob(jobId, Guid.NewGuid(), Guid.NewGuid(), "stale-run", JobState.Queued);
        var calls = new List<string>();
        var jobs = new SingleClaimJobControl(new JobClaim(job, lease, false), calls);
        var mirror = new RecordingCheckpointMirror(calls);
        var worker = new JobWorker(
            jobs,
            new StalePlanJobRunCatalog(),
            new NoopConnectionRevalidator(),
            new CountingTargetFactory(),
            new CountingSourceFactory(),
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            new RecordingJobEventWriter(),
            new NoWorkerFaults(),
            new BlockingWorkerDelay(),
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await jobs.Failed;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("plan_stale", jobs.FailureCode);
        Assert.Contains("Seal the plan again before starting a transfer.", jobs.FailureDetail);
    }

    [Theory]
    [InlineData(TransferUnitKind.Batch, true, "Writing rows of dbo.T (batch 1) failed: boom")]
    [InlineData(
        TransferUnitKind.DeferredColumns,
        false,
        "Writing deferred columns of the target (batch 1) failed: boom"
    )]
    public async Task JobWorker_WhenTheTargetFailsToApplyAUnit_NamesTheUnitInTheFailureDetail(
        TransferUnitKind kind,
        bool withTable,
        string expected
    )
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var runId = Guid.NewGuid();
        var ttl = TimeSpan.FromMinutes(1);
        var lease = new LeaseGrant(
            jobId,
            "worker-a",
            1,
            fixture.Clock.UtcNow.Add(ttl),
            fixture.Clock.UtcNow.AddMinutes(1)
        );
        var job = new TransferJob(jobId, runId, Guid.NewGuid(), "apply-failure", JobState.Queued);
        var run = new TransferRun(jobId, runId, "seal", true, Guid.NewGuid(), Guid.NewGuid(), TransferMode.DirectFast);
        var unit = new TransferUnit(
            1,
            new StableKey([new KeyComponent("Id", 1)]),
            1,
            kind,
            Table: withTable ? new TableAddress("dbo", "T") : null
        );
        var checkpoint = new TargetCheckpoint(jobId, runId, 0, null, 0, "seal", lease.FenceToken);
        var calls = new List<string>();
        var jobs = new SingleClaimJobControl(new JobClaim(job, lease, false), calls);
        var target = new TestTargetRunSession(checkpoint, "target", calls)
        {
            ApplyFailure = new InvalidOperationException("boom"),
        };
        var mirror = new RecordingCheckpointMirror(calls);
        var worker = new JobWorker(
            jobs,
            new TestJobRunCatalog(run),
            new NoopConnectionRevalidator(),
            new TestTargetRunSessionFactory(target),
            new TestTransferReadSessionFactory(new TestTransferReadSession(unit, "source")),
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            new RecordingJobEventWriter(),
            new NoWorkerFaults(),
            new BlockingWorkerDelay(),
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await jobs.Failed;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("transfer_failed", jobs.FailureCode);
        Assert.Equal(expected, jobs.FailureDetail);
    }

    [Fact]
    public async Task JobWorker_WhenVerificationFailsOnAControlWithoutADedicatedTransition_FallsBackToFailed()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var runId = Guid.NewGuid();
        var ttl = TimeSpan.FromMinutes(1);
        var lease = new LeaseGrant(
            jobId,
            "worker-a",
            1,
            fixture.Clock.UtcNow.Add(ttl),
            fixture.Clock.UtcNow.AddMinutes(1)
        );
        var job = new TransferJob(jobId, runId, Guid.NewGuid(), "verify-fallback", JobState.Queued);
        var run = new TransferRun(jobId, runId, "seal", true, Guid.NewGuid(), Guid.NewGuid(), TransferMode.DirectFast);
        var unit = new TransferUnit(1, new StableKey([new KeyComponent("Id", 1)]), 1, TransferUnitKind.Batch);
        var checkpoint = new TargetCheckpoint(jobId, runId, 1, unit.LastStableKey, 1, "seal", lease.FenceToken);
        var calls = new List<string>();
        var jobs = new SingleClaimJobControl(new JobClaim(job, lease, false), calls);
        var target = new TestTargetRunSession(checkpoint, "target", calls) { VerificationFailure = "off plan" };
        var mirror = new RecordingCheckpointMirror(calls);
        var worker = new JobWorker(
            jobs,
            new TestJobRunCatalog(run),
            new NoopConnectionRevalidator(),
            new TestTargetRunSessionFactory(target),
            new TestTransferReadSessionFactory(new TestTransferReadSession(unit, "source")),
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            new RecordingJobEventWriter(),
            new NoWorkerFaults(),
            new BlockingWorkerDelay(),
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await jobs.Failed;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("verification_failed", jobs.FailureCode);
        Assert.Equal("off plan", jobs.FailureDetail);
    }

    [Fact]
    public async Task JobWorker_WhenARowIsSkippedOnAUnitWithoutATable_AnnouncesTheConflictAgainstTheTarget()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "skipped-run")).Job;
        var ttl = TimeSpan.FromMinutes(1);
        var run = new TransferRun(
            job.JobId,
            job.RunId,
            "seal",
            true,
            Guid.NewGuid(),
            Guid.NewGuid(),
            TransferMode.DirectFast
        );
        var unit = new TransferUnit(1, new StableKey([new KeyComponent("Id", 1)]), 1, TransferUnitKind.Batch);
        var checkpoint = new TargetCheckpoint(job.JobId, job.RunId, 1, null, 0, "seal", 1, SkippedRows: 1);
        var events = new ThrowOnSucceededJobEventWriter();
        var mirror = new RecordingCheckpointMirror([]);
        var delay = new GateWorkerDelay();
        var worker = new JobWorker(
            store,
            new TestJobRunCatalog(run),
            new NoopConnectionRevalidator(),
            new TestTargetRunSessionFactory(new TestTargetRunSession(checkpoint, "target", [])),
            new TestTransferReadSessionFactory(new TestTransferReadSession(unit, "source")),
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            events,
            new NoWorkerFaults(),
            delay,
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await delay.FirstDue;
        await worker.StopAsync(CancellationToken.None);

        // The announcement of the terminal state failed, which never fails the job itself.
        Assert.Equal(JobState.Succeeded, await store.GetStateAsync(job.JobId, CancellationToken.None));
        var conflict = Assert.Single(events.Appends, item => item.EventType == "conflict");
        Assert.Equal("1 row(s) in the target already existed in the target and were skipped.", conflict.Payload.Detail);
    }

    [Fact]
    public async Task JobWorker_WhenTheFailureMessageIsVeryLong_KeepsTheFirstTwoThousandCharacters()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var runId = Guid.NewGuid();
        var ttl = TimeSpan.FromMinutes(1);
        var lease = new LeaseGrant(
            jobId,
            "worker-a",
            1,
            fixture.Clock.UtcNow.Add(ttl),
            fixture.Clock.UtcNow.AddMinutes(1)
        );
        var job = new TransferJob(jobId, runId, Guid.NewGuid(), "long-failure", JobState.Queued);
        var run = new TransferRun(jobId, runId, "seal", true, Guid.NewGuid(), Guid.NewGuid(), TransferMode.DirectFast);
        var calls = new List<string>();
        var jobs = new SingleClaimJobControl(new JobClaim(job, lease, false), calls);
        var mirror = new RecordingCheckpointMirror(calls);
        var worker = new JobWorker(
            jobs,
            new TestJobRunCatalog(run),
            new ThrowingConnectionRevalidator(new InvalidOperationException(new string('x', 2500))),
            new CountingTargetFactory(),
            new CountingSourceFactory(),
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            new RecordingJobEventWriter(),
            new NoWorkerFaults(),
            new BlockingWorkerDelay(),
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await jobs.Failed;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("transfer_failed", jobs.FailureCode);
        Assert.Equal(2000, jobs.FailureDetail!.Length);
    }

    private sealed class ThrowingConnectionRevalidator(Exception exception) : ITransferConnectionRevalidator
    {
        public Task RevalidateAsync(TransferRun run, CancellationToken cancellationToken) =>
            Task.FromException(exception);
    }

    private sealed class ThrowOnSucceededJobEventWriter : IJobEventWriter
    {
        public List<JobEventAppend> Appends { get; } = [];

        public Task<JobEvent> AppendAsync(JobEventAppend append, CancellationToken cancellationToken)
        {
            if (append.Payload.State == "succeeded")
                return Task.FromException<JobEvent>(new InvalidOperationException("event store unavailable"));
            Appends.Add(append);
            return Task.FromResult(
                new JobEvent(append.JobId, Appends.Count, append.EventType, append.Payload, DateTimeOffset.UnixEpoch)
            );
        }
    }

    private sealed class StalePlanJobRunCatalog : IJobRunCatalog
    {
        public Task<TransferRun> LoadAsync(TransferJob job, CancellationToken cancellationToken) =>
            Task.FromException<TransferRun>(new StalePlanException(0));
    }

    [Fact]
    public async Task JobWorker_WhenTransferCompletes_MarksTheJobSucceeded()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "succeeded-run")).Job;
        var ttl = TimeSpan.FromMinutes(1);
        var run = new TransferRun(
            job.JobId,
            job.RunId,
            "seal",
            true,
            Guid.NewGuid(),
            Guid.NewGuid(),
            TransferMode.DirectFast
        );
        var unit = new TransferUnit(1, new StableKey([new KeyComponent("Id", 1)]), 1, TransferUnitKind.Batch);
        var checkpoint = new TargetCheckpoint(job.JobId, job.RunId, 0, null, 0, "seal", 1);
        var source = new TestTransferReadSession(unit, "source");
        var target = new TestTargetRunSession(checkpoint, "target", []);
        var mirror = new RecordingCheckpointMirror([]);
        var delay = new GateWorkerDelay();
        var worker = new JobWorker(
            store,
            new TestJobRunCatalog(run),
            new NoopConnectionRevalidator(),
            new TestTargetRunSessionFactory(target),
            new TestTransferReadSessionFactory(source),
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            new RecordingJobEventWriter(),
            new NoWorkerFaults(),
            delay,
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await delay.FirstDue;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(JobState.Succeeded, await store.GetStateAsync(job.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task Pause_WhenRequestedAtACommitBoundary_DiscardsThePrefetchedUnitAndPauses()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var runId = Guid.NewGuid();
        var ttl = TimeSpan.FromMinutes(1);
        var lease = new LeaseGrant(
            jobId,
            "worker-a",
            1,
            fixture.Clock.UtcNow.Add(ttl),
            fixture.Clock.UtcNow.AddMinutes(1)
        );
        var job = new TransferJob(jobId, runId, Guid.NewGuid(), "pause-run", JobState.Queued);
        var run = new TransferRun(jobId, runId, "seal", true, Guid.NewGuid(), Guid.NewGuid(), TransferMode.DirectFast);
        var first = new TransferUnit(1, new StableKey([new KeyComponent("Id", 1)]), 1, TransferUnitKind.Batch);
        var prefetched = new TransferUnit(2, new StableKey([new KeyComponent("Id", 2)]), 1, TransferUnitKind.Batch);
        var checkpoint = new TargetCheckpoint(jobId, runId, 0, null, 0, "seal", lease.FenceToken);
        var jobs = new BoundaryJobControl(new JobClaim(job, lease, false));
        var source = new PrefetchedReadSession(first, prefetched);
        var target = new CommitBarrierTargetSession(checkpoint);
        var calls = new List<string>();
        var mirror = new RecordingCheckpointMirror(calls);
        var worker = new JobWorker(
            jobs,
            new TestJobRunCatalog(run),
            new NoopConnectionRevalidator(),
            new CommitBarrierTargetSessionFactory(target),
            new PrefetchedReadSessionFactory(source),
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            new RecordingJobEventWriter(),
            new NoWorkerFaults(),
            new BlockingWorkerDelay(),
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await target.ApplyStarted;
        jobs.State = JobState.Pausing;
        target.ReleaseCommit();
        await jobs.TerminalState;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(JobState.Paused, jobs.State);
        Assert.Equal([1L], target.DurableBatchSequences);
        Assert.Contains(mirror.Checkpoints, checkpoint => checkpoint.BatchSequence == 1);
        Assert.True(source.Discarded);
        Assert.False(source.HasPrefetchedUnit);
        Assert.True(source.Disposed);
        Assert.DoesNotContain(2L, target.DurableBatchSequences);
    }

    [Fact]
    public async Task Cancel_WhenRequestedDuringApply_CommitsAtTheBoundaryAndUsesAnUncancelledCleanupToken()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var runId = Guid.NewGuid();
        var ttl = TimeSpan.FromMinutes(1);
        var lease = new LeaseGrant(
            jobId,
            "worker-a",
            1,
            fixture.Clock.UtcNow.Add(ttl),
            fixture.Clock.UtcNow.AddMinutes(1)
        );
        var job = new TransferJob(jobId, runId, Guid.NewGuid(), "cancel-run", JobState.Queued);
        var run = new TransferRun(jobId, runId, "seal", true, Guid.NewGuid(), Guid.NewGuid(), TransferMode.DirectFast);
        var first = new TransferUnit(1, new StableKey([new KeyComponent("Id", 1)]), 1, TransferUnitKind.Batch);
        var prefetched = new TransferUnit(2, new StableKey([new KeyComponent("Id", 2)]), 1, TransferUnitKind.Batch);
        var checkpoint = new TargetCheckpoint(jobId, runId, 0, null, 0, "seal", lease.FenceToken);
        var jobs = new BoundaryJobControl(new JobClaim(job, lease, false));
        var source = new PrefetchedReadSession(first, prefetched);
        var target = new CommitBarrierTargetSession(checkpoint);
        var mirror = new RecordingCheckpointMirror([]);
        var worker = new JobWorker(
            jobs,
            new TestJobRunCatalog(run),
            new NoopConnectionRevalidator(),
            new CommitBarrierTargetSessionFactory(target),
            new PrefetchedReadSessionFactory(source),
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            new RecordingJobEventWriter(),
            new NoWorkerFaults(),
            new BlockingWorkerDelay(),
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await target.ApplyStarted;
        jobs.State = JobState.Cancelling;
        target.ReleaseCommit();
        await jobs.TerminalState;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(JobState.Cancelled, jobs.State);
        Assert.Equal([1L], target.DurableBatchSequences);
        Assert.DoesNotContain(2L, target.DurableBatchSequences);
        Assert.True(source.Discarded);
        Assert.True(target.Discarded);
        Assert.NotEqual(target.ApplyToken, source.DiscardToken);
        Assert.Equal(source.DiscardToken, target.DiscardToken);
        Assert.Equal(source.DiscardToken, jobs.CancelledToken);
        Assert.False(source.DiscardToken!.Value.IsCancellationRequested);
    }

    [Fact]
    public async Task Resume_WhenPausedJobIsReclaimed_KeysetSeeksAfterTheTargetStableKey()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var ttl = TimeSpan.FromMinutes(1);
        var job = store.Start(new StartJobRequest(Guid.NewGuid(), "resume-run")).Job;
        var pausedClaim = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(pausedClaim, CancellationToken.None);
        await store.MarkRunningAsync(pausedClaim.Lease, CancellationToken.None);
        await store.RequestPauseAsync(job.JobId, CancellationToken.None);
        await store.MarkPausedAsync(pausedClaim.Lease, CancellationToken.None);
        await store.RequestResumeAsync(job.JobId, CancellationToken.None);
        var stableKey = new StableKey([new KeyComponent("Id", 1)]);
        var run = new TransferRun(
            job.JobId,
            job.RunId,
            "seal",
            true,
            Guid.NewGuid(),
            Guid.NewGuid(),
            TransferMode.DirectFast
        );
        var source = new PrefetchedReadSession(
            new TransferUnit(2, new StableKey([new KeyComponent("Id", 2)]), 1, TransferUnitKind.Batch),
            new TransferUnit(3, new StableKey([new KeyComponent("Id", 3)]), 1, TransferUnitKind.Batch)
        );
        var target = new CommitBarrierTargetSession(
            new TargetCheckpoint(job.JobId, job.RunId, 1, stableKey, 1, "seal", 2)
        );
        var sourceFactory = new PrefetchedReadSessionFactory(source);
        var mirror = new RecordingCheckpointMirror([]);
        var worker = new JobWorker(
            store,
            new TestJobRunCatalog(run),
            new NoopConnectionRevalidator(),
            new CommitBarrierTargetSessionFactory(target),
            sourceFactory,
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            new RecordingJobEventWriter(),
            new NoWorkerFaults(),
            new BlockingWorkerDelay(),
            fixture.Clock,
            "worker-b",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await target.ApplyStarted;
        Assert.Equal(stableKey, sourceFactory.LastRequestedStartAfter);
        target.ReleaseCommit();
        await target.FirstCommit;
        await worker.StopAsync(CancellationToken.None);

        Assert.Single(store.GetHistory(job.JobId), transition => transition == (JobState.Paused, JobState.Queued));
    }

    [Fact]
    public async Task AtomicComponent_WhenPauseIsRequestedDuringApply_PausesAfterItsSingleCommit()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var runId = Guid.NewGuid();
        var ttl = TimeSpan.FromMinutes(1);
        var lease = new LeaseGrant(
            jobId,
            "worker-a",
            1,
            fixture.Clock.UtcNow.Add(ttl),
            fixture.Clock.UtcNow.AddMinutes(1)
        );
        var job = new TransferJob(jobId, runId, Guid.NewGuid(), "atomic-pause-run", JobState.Queued);
        var run = new TransferRun(jobId, runId, "seal", true, Guid.NewGuid(), Guid.NewGuid(), TransferMode.DirectFast);
        var component = new TransferUnit(
            1,
            new StableKey([new KeyComponent("Id", 1)]),
            2,
            TransferUnitKind.AtomicComponent
        );
        var checkpoint = new TargetCheckpoint(jobId, runId, 0, null, 0, "seal", lease.FenceToken);
        var jobs = new BoundaryJobControl(new JobClaim(job, lease, false));
        var source = new PrefetchedReadSession(
            component,
            new TransferUnit(2, new StableKey([new KeyComponent("Id", 2)]), 1, TransferUnitKind.Batch)
        );
        var target = new CommitBarrierTargetSession(checkpoint);
        var mirror = new RecordingCheckpointMirror([]);
        var worker = new JobWorker(
            jobs,
            new TestJobRunCatalog(run),
            new NoopConnectionRevalidator(),
            new CommitBarrierTargetSessionFactory(target),
            new PrefetchedReadSessionFactory(source),
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            new RecordingJobEventWriter(),
            new NoWorkerFaults(),
            new BlockingWorkerDelay(),
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await target.ApplyStarted;
        jobs.State = JobState.Pausing;
        Assert.Empty(target.DurableBatchSequences);
        target.ReleaseCommit();
        await jobs.TerminalState;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(JobState.Paused, jobs.State);
        Assert.Equal([1L], target.DurableBatchSequences);
    }

    [Fact]
    public async Task JobWorker_WhenTargetBatchCommits_PublishesCheckpointByteProgress()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var runId = Guid.NewGuid();
        var ttl = TimeSpan.FromMinutes(1);
        var lease = new LeaseGrant(
            jobId,
            "worker-a",
            1,
            fixture.Clock.UtcNow.Add(ttl),
            fixture.Clock.UtcNow.AddMinutes(1)
        );
        var job = new TransferJob(jobId, runId, Guid.NewGuid(), "byte-progress", JobState.Queued);
        var run = new TransferRun(jobId, runId, "seal", true, Guid.NewGuid(), Guid.NewGuid(), TransferMode.DirectFast);
        var unit = new TransferUnit(
            1,
            new StableKey([new KeyComponent("Id", 1)]),
            1,
            TransferUnitKind.Batch,
            BytesTransferred: 100
        );
        var checkpoint = new TargetCheckpoint(
            jobId,
            runId,
            1,
            unit.LastStableKey,
            1,
            "seal",
            lease.FenceToken,
            BytesTransferred: 100
        );
        var calls = new List<string>();
        var jobs = new SingleClaimJobControl(new JobClaim(job, lease, false), calls);
        var source = new TestTransferReadSession(unit, "source-connection");
        var sourceFactory = new TestTransferReadSessionFactory(source);
        var target = new TestTargetRunSession(checkpoint, "target-connection", calls)
        {
            Snapshot = new(new(jobId, runId, 0, null, 0, "seal", lease.FenceToken), []),
        };
        var mirror = new RecordingCheckpointMirror(calls);
        var events = new RecordingJobEventWriter();
        var worker = new JobWorker(
            jobs,
            new TestJobRunCatalog(run),
            new NoopConnectionRevalidator(),
            new TestTargetRunSessionFactory(target),
            sourceFactory,
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            events,
            new NoWorkerFaults(),
            new BlockingWorkerDelay(),
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await jobs.MarkedVerifying;
        await worker.StopAsync(CancellationToken.None);

        var progress = Assert.Single(events.Appends, item => item.EventType == "progress");
        Assert.Equal(100, progress.Payload.BytesTransferred);
        Assert.Equal(
            ["preparing", "running", "verifying", "succeeded"],
            events.Appends.Where(item => item.EventType == "state").Select(item => item.Payload.State)
        );
    }

    [Fact]
    public async Task JobWorker_WhenConnectionRevalidationFails_DoesNotOpenEitherDatabaseSession()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var jobId = fixture.SeedJob();
        var runId = Guid.NewGuid();
        var ttl = TimeSpan.FromMinutes(1);
        var job = new TransferJob(jobId, runId, Guid.NewGuid(), "revalidate-run", JobState.Queued);
        var lease = new LeaseGrant(
            jobId,
            "worker-a",
            1,
            fixture.Clock.UtcNow.Add(ttl),
            fixture.Clock.UtcNow.AddMinutes(1)
        );
        var run = new TransferRun(jobId, runId, "seal", true, Guid.NewGuid(), Guid.NewGuid(), TransferMode.DirectFast);
        var calls = new List<string>();
        var jobs = new SingleClaimJobControl(new JobClaim(job, lease, false), calls);
        var revalidator = new FailingConnectionRevalidator();
        var targets = new CountingTargetFactory();
        var sources = new CountingSourceFactory();
        var mirror = new RecordingCheckpointMirror(calls);
        var events = new RecordingJobEventWriter();
        var worker = new JobWorker(
            jobs,
            new TestJobRunCatalog(run),
            revalidator,
            targets,
            sources,
            new RecoveryCoordinator(mirror),
            new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()),
            mirror,
            events,
            new NoWorkerFaults(),
            new BlockingWorkerDelay(),
            fixture.Clock,
            "worker-a",
            ttl,
            TimeSpan.FromMinutes(1)
        );

        await worker.StartAsync(CancellationToken.None);
        await revalidator.Called;
        await jobs.Failed;
        for (var attempt = 0; attempt < 100 && !events.Appends.Any(item => item.Payload.State == "failed"); attempt++)
            await Task.Delay(20);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, revalidator.Calls);
        Assert.Equal(run.SourceConnectionId, revalidator.Run!.SourceConnectionId);
        Assert.Equal(run.TargetConnectionId, revalidator.Run.TargetConnectionId);
        Assert.Equal(0, targets.OpenCalls);
        Assert.Equal(0, sources.OpenCalls);
        Assert.Equal("connection_unhealthy", jobs.FailureCode);
        Assert.Equal("Connection health revalidation failed.", jobs.FailureDetail);
        var failed = Assert.Single(events.Appends, item => item.Payload.State == "failed");
        Assert.Equal("state", failed.EventType);
        Assert.Equal("Connection health revalidation failed.", failed.Payload.Detail);
    }

    private sealed class RecordingJobEventWriter : IJobEventWriter
    {
        public List<JobEventAppend> Appends { get; } = [];

        public Task<JobEvent> AppendAsync(JobEventAppend append, CancellationToken cancellationToken)
        {
            Appends.Add(append);
            return Task.FromResult(
                new JobEvent(append.JobId, Appends.Count, append.EventType, append.Payload, DateTimeOffset.UnixEpoch)
            );
        }
    }

    private sealed class FailingConnectionRevalidator : ITransferConnectionRevalidator
    {
        private readonly TaskCompletionSource<bool> _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Called => _called.Task;
        public int Calls { get; private set; }
        public TransferRun? Run { get; private set; }

        public Task RevalidateAsync(TransferRun run, CancellationToken cancellationToken)
        {
            Calls++;
            Run = run;
            _called.TrySetResult(true);
            throw new ConnectionNotHealthyException();
        }
    }

    private sealed class NoopConnectionRevalidator : ITransferConnectionRevalidator
    {
        public Task RevalidateAsync(TransferRun run, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CountingTargetFactory : ITargetRunSessionFactory
    {
        public int OpenCalls { get; private set; }

        public Task<ITargetRunSession> OpenAsync(TransferRun run, CancellationToken cancellationToken)
        {
            OpenCalls++;
            return Task.FromException<ITargetRunSession>(new NotSupportedException());
        }
    }

    private sealed class CountingSourceFactory : ITransferReadSessionFactory
    {
        public int OpenCalls { get; private set; }

        public Task<ITransferReadSession> OpenKeysetAsync(
            TransferRun run,
            StableKey? startAfter,
            CancellationToken cancellationToken,
            TableAddress? table = null,
            TransferPhase phase = TransferPhase.Rows
        )
        {
            OpenCalls++;
            return Task.FromException<ITransferReadSession>(new NotSupportedException());
        }
    }
}
