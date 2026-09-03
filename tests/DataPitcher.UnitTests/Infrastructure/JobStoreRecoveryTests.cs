using DataPitcher.Application.Worker;
using DataPitcher.ControlStore;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class JobStoreRecoveryTests
{
    [Fact]
    public async Task JobStore_WhenDuplicateStartsRace_ClaimsTheJobOnlyOnce()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var request = new StartJobRequest(Guid.NewGuid(), "start-recovery-race");
        var releaseStarts = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startTasks = new[]
        {
            StartAfterAsync(releaseStarts.Task, store, request),
            StartAfterAsync(releaseStarts.Task, store, request),
        };
        releaseStarts.SetResult(true);
        var starts = await Task.WhenAll(startTasks);

        var releaseClaims = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimTasks = new[]
        {
            ClaimAfterAsync(releaseClaims.Task, store),
            ClaimAfterAsync(releaseClaims.Task, store),
        };
        releaseClaims.SetResult(true);
        var claims = await Task.WhenAll(claimTasks);

        Assert.Single(starts.Select(result => result.Job.JobId).Distinct());
        Assert.Single(claims, claim => claim?.Job.JobId == starts[0].Job.JobId);
    }

    [Fact]
    public async Task JobStore_WhenActiveLeaseExpires_ReclaimsItAsInterrupted()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "start-interrupted")).Job;
        var ttl = TimeSpan.FromMinutes(1);
        var first = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(first, CancellationToken.None);
        await store.MarkRunningAsync(first.Lease, CancellationToken.None);

        Assert.Null(await store.TryClaimNextAsync("worker-b", ttl, CancellationToken.None));
        fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1)));
        var recovered = await store.TryClaimNextAsync("worker-b", ttl, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.True(recovered!.IsInterrupted);
        Assert.Equal(job.JobId, recovered.Job.JobId);
    }

    [Fact]
    public async Task JobStore_WhenInterruptedClaimIsPrepared_RequeuesItBeforePreparing()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var ttl = TimeSpan.FromMinutes(1);
        var job = store.Start(new(Guid.NewGuid(), "start-recovery-prepare")).Job;
        var first = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(first, CancellationToken.None);
        await store.MarkRunningAsync(first.Lease, CancellationToken.None);
        fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1)));
        var interrupted = (await store.TryClaimNextAsync("worker-b", ttl, CancellationToken.None))!;

        await store.PrepareAsync(interrupted, CancellationToken.None);

        Assert.True(interrupted.IsInterrupted);
        Assert.Equal(JobState.Preparing, await store.GetStateAsync(job.JobId, CancellationToken.None));
        Assert.Contains(store.GetHistory(job.JobId), transition => transition == (JobState.Running, JobState.Queued));
    }

    [Fact]
    public async Task JobStore_WhenControlUpdateIsSuppressed_RejectsTheSupersededIntent()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var ttl = TimeSpan.FromMinutes(1);
        var job = store.Start(new(Guid.NewGuid(), "start-superseded-intent")).Job;
        var claim = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(claim, CancellationToken.None);
        await store.MarkRunningAsync(claim.Lease, CancellationToken.None);
        using (var db = fixture.Database.Open())
            db.Execute(
                "CREATE TRIGGER SuppressControlIntent BEFORE UPDATE OF State ON Jobs BEGIN SELECT RAISE(IGNORE); END;"
            );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RequestPauseAsync(job.JobId, CancellationToken.None)
        );

        Assert.Equal("Job control intent was superseded.", exception.Message);
        Assert.Equal(JobState.Running, await store.GetStateAsync(job.JobId, CancellationToken.None));
        Assert.DoesNotContain(store.GetHistory(job.JobId), transition => transition.To == JobState.Pausing);
    }

    [Fact]
    public async Task JobStore_WhenWorkerLeaseExpires_RejectsTheWorkerTransition()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var ttl = TimeSpan.FromMinutes(1);
        var job = store.Start(new(Guid.NewGuid(), "start-stale-worker")).Job;
        var claim = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(claim, CancellationToken.None);
        fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MarkRunningAsync(claim.Lease, CancellationToken.None)
        );

        Assert.Equal("Worker no longer owns the job.", exception.Message);
        Assert.Equal(JobState.Preparing, await store.GetStateAsync(job.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task JobStore_WhenOwnerReacquiresAfterExpiry_RejectsTheStaleFenceEvenWithMatchingOwner()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var ttl = TimeSpan.FromMinutes(1);
        var job = store.Start(new(Guid.NewGuid(), "start-stale-fence-same-owner")).Job;
        var first = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(first, CancellationToken.None);
        fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1)));
        var reacquired = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MarkRunningAsync(first.Lease, CancellationToken.None)
        );

        Assert.Equal(first.Lease.OwnerId, reacquired.Lease.OwnerId);
        Assert.True(reacquired.Lease.FenceToken > first.Lease.FenceToken);
        Assert.Equal("Worker no longer owns the job.", exception.Message);
        Assert.Equal(JobState.Preparing, await store.GetStateAsync(job.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task JobStore_WhenLeaseReleaseIsSuperseded_RejectsTheWorkerTransition()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var ttl = TimeSpan.FromMinutes(1);
        var job = store.Start(new(Guid.NewGuid(), "start-superseded-release")).Job;
        var claim = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(claim, CancellationToken.None);
        await store.MarkRunningAsync(claim.Lease, CancellationToken.None);
        await store.RequestPauseAsync(job.JobId, CancellationToken.None);
        using (var db = fixture.Database.Open())
            db.Execute(
                "CREATE TRIGGER SupersedeWorkerLease AFTER UPDATE OF State ON Jobs BEGIN UPDATE JobLeases SET OwnerId = 'worker-b', FenceToken = FenceToken + 1 WHERE JobId = NEW.JobId; END;"
            );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MarkPausedAsync(claim.Lease, CancellationToken.None)
        );

        Assert.Equal("Worker lease release was superseded.", exception.Message);
        Assert.Equal(JobState.Pausing, await store.GetStateAsync(job.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task JobStore_WhenOperatorRecordsControlIntent_DoesNotRequireAWorkerLease()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var ttl = TimeSpan.FromMinutes(1);
        var job = store.Start(new(Guid.NewGuid(), "start-controls")).Job;
        var claim = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(claim, CancellationToken.None);
        await store.MarkRunningAsync(claim.Lease, CancellationToken.None);
        await store.RequestPauseAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobState.Pausing, await store.GetStateAsync(job.JobId, CancellationToken.None));
        await store.MarkPausedAsync(claim.Lease, CancellationToken.None);
        await store.RequestResumeAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobState.Queued, await store.GetStateAsync(job.JobId, CancellationToken.None));

        var resumed = (await store.TryClaimNextAsync("worker-b", ttl, CancellationToken.None))!;
        await store.PrepareAsync(resumed, CancellationToken.None);
        await store.MarkRunningAsync(resumed.Lease, CancellationToken.None);
        await store.RequestCancelAsync(job.JobId, CancellationToken.None);
        await store.MarkCancelledAsync(resumed.Lease, CancellationToken.None);

        Assert.Equal(JobState.Cancelled, await store.GetStateAsync(job.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task JobStore_WhenWorkerFailsOrVerifies_PersistsTheGuardedTransitionAndFailureCode()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var ttl = TimeSpan.FromMinutes(1);
        var failed = store.Start(new(Guid.NewGuid(), "start-failed")).Job;
        var failedClaim = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(failedClaim, CancellationToken.None);
        await store.MarkRunningAsync(failedClaim.Lease, CancellationToken.None);
        await store.MarkFailedAsync(
            failedClaim.Lease,
            "NonResumableInterrupted",
            "Login failed for user 'app'.",
            CancellationToken.None
        );
        var verifying = store.Start(new(Guid.NewGuid(), "start-verifying")).Job;
        var verifyingClaim = (await store.TryClaimNextAsync("worker-b", ttl, CancellationToken.None))!;
        await store.PrepareAsync(verifyingClaim, CancellationToken.None);
        await store.MarkRunningAsync(verifyingClaim.Lease, CancellationToken.None);
        await store.MarkVerifyingAsync(verifyingClaim.Lease, CancellationToken.None);

        using var db = fixture.Database.Open();
        Assert.Equal("Login failed for user 'app'.", store.Get(failed.JobId).FailureDetail);
        Assert.Equal(
            "NonResumableInterrupted",
            db.Query<string>(
                    "SELECT FailureCode FROM Jobs WHERE JobId = @jobId",
                    new ControlParameter("jobId", failed.JobId.ToString())
                )
                .Single()
        );
        Assert.Equal(JobState.Verifying, await store.GetStateAsync(verifying.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task JobStore_WhenVerificationFails_PersistsTheVerificationFailedStateWithTheReason()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "start-verification-failed")).Job;
        var claim = (await store.TryClaimNextAsync("worker-a", TimeSpan.FromMinutes(1), CancellationToken.None))!;
        await store.PrepareAsync(claim, CancellationToken.None);
        await store.MarkRunningAsync(claim.Lease, CancellationToken.None);
        await store.MarkVerifyingAsync(claim.Lease, CancellationToken.None);

        await store.MarkVerificationFailedAsync(
            claim.Lease,
            "1 planned key was never written.",
            CancellationToken.None
        );

        var persisted = store.Get(job.JobId);
        Assert.Equal(JobState.VerificationFailed, persisted.State);
        Assert.Equal("verification_failed", persisted.FailureCode);
        Assert.Equal("1 planned key was never written.", persisted.FailureDetail);
        Assert.Null(await store.TryClaimNextAsync("worker-b", TimeSpan.FromMinutes(1), CancellationToken.None));
    }

    [Fact]
    public async Task JobStore_WhenWorkerSucceeds_PersistsTheTerminalTransitionAndReleasesTheLease()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "start-succeeded")).Job;
        var claim = (await store.TryClaimNextAsync("worker-a", TimeSpan.FromMinutes(1), CancellationToken.None))!;
        await store.PrepareAsync(claim, CancellationToken.None);
        await store.MarkRunningAsync(claim.Lease, CancellationToken.None);
        await store.MarkVerifyingAsync(claim.Lease, CancellationToken.None);
        var method = Assert.Single(typeof(JobStore).GetMethods(), item => item.Name == "MarkSucceededAsync");

        await (Task)method.Invoke(store, [claim.Lease, CancellationToken.None])!;

        using var db = fixture.Database.Open();
        Assert.Equal(JobState.Succeeded, await store.GetStateAsync(job.JobId, CancellationToken.None));
        Assert.Contains(
            store.GetHistory(job.JobId),
            transition => transition == (JobState.Verifying, JobState.Succeeded)
        );
        Assert.Null(
            db.Query<string?>(
                    "SELECT OwnerId FROM JobLeases WHERE JobId = @jobId",
                    new ControlParameter("jobId", job.JobId.ToString())
                )
                .Single()
        );
    }

    [Fact]
    public async Task CheckpointMirrorStore_WhenTargetCheckpointAdvances_ReplacesTheDisplayCopy()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var job = fixture.SeedJob();
        var run = Guid.NewGuid();
        var mirror = new CheckpointMirrorStore(fixture.Database);
        await mirror.OverwriteAsync(
            new(job, run, 1, new StableKey([new KeyComponent("Id", 1)]), 10, "seal", 1),
            CancellationToken.None
        );
        await mirror.OverwriteAsync(
            new(job, run, 2, new StableKey([new KeyComponent("Id", 2)]), 20, "seal", 2),
            CancellationToken.None
        );

        using var db = fixture.Database.Open();
        Assert.Equal(
            2,
            db.Query<long>(
                    "SELECT LastCommittedBatchSequence FROM BatchCheckpointMirrors WHERE JobId = @jobId AND RunId = @runId",
                    new ControlParameter("jobId", job.ToString()),
                    new ControlParameter("runId", run.ToString())
                )
                .Single()
        );
    }

    [Fact]
    public async Task CheckpointMirrorStore_WhenTargetHasNoCommittedKey_PersistsANullDisplayKey()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var job = fixture.SeedJob();
        var run = Guid.NewGuid();
        var mirror = new CheckpointMirrorStore(fixture.Database);

        await mirror.OverwriteAsync(new(job, run, 0, null, 0, "seal", 1), CancellationToken.None);

        using var db = fixture.Database.Open();
        Assert.Equal(
            1,
            db.Query<long>(
                    "SELECT COUNT(*) FROM BatchCheckpointMirrors WHERE JobId = @jobId AND RunId = @runId AND LastCommittedStableKey IS NULL",
                    new ControlParameter("jobId", job.ToString()),
                    new ControlParameter("runId", run.ToString())
                )
                .Single()
        );
    }

    [Fact]
    public void BatchCheckpointMirror_WhenPersisted_RoundTripsEveryDerivedCheckpointValue()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var job = fixture.SeedJob();
        var runId = Guid.NewGuid().ToString();
        var updated = fixture.Clock.UtcNow.ToString("O");

        using var db = fixture.Database.Open();
        db.Execute(
            "INSERT INTO BatchCheckpointMirrors (JobId, RunId, LastCommittedBatchSequence, LastCommittedStableKey, CumulativeRowCount, SealedManifestHash, FenceToken, UpdatedUtc) VALUES (@job, @run, 2, 'Id=2', 20, 'seal', 3, @updated)",
            new ControlParameter("job", job.ToString()),
            new ControlParameter("run", runId),
            new ControlParameter("updated", updated)
        );
        var persisted = Assert.Single(
            db.Query(
                "SELECT JobId, RunId, LastCommittedBatchSequence, LastCommittedStableKey, CumulativeRowCount, SealedManifestHash, FenceToken, UpdatedUtc FROM BatchCheckpointMirrors",
                reader =>
                    (
                        JobId: reader.GetString(0),
                        RunId: reader.GetString(1),
                        Sequence: reader.GetInt64(2),
                        Key: reader.GetString(3),
                        Rows: reader.GetInt64(4),
                        Seal: reader.GetString(5),
                        Fence: reader.GetInt64(6),
                        Updated: reader.GetString(7)
                    )
            )
        );

        Assert.Equal(job.ToString(), persisted.JobId);
        Assert.Equal(runId, persisted.RunId);
        Assert.Equal(2, persisted.Sequence);
        Assert.Equal("Id=2", persisted.Key);
        Assert.Equal(20, persisted.Rows);
        Assert.Equal("seal", persisted.Seal);
        Assert.Equal(3, persisted.Fence);
        Assert.Equal(updated, persisted.Updated);
    }

    private static async Task<StartJobResult> StartAfterAsync(Task barrier, JobStore store, StartJobRequest request)
    {
        await barrier;
        return store.Start(request);
    }

    private static async Task<JobClaim?> ClaimAfterAsync(Task barrier, JobStore store)
    {
        await barrier;
        return await store.TryClaimNextAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None);
    }
}
