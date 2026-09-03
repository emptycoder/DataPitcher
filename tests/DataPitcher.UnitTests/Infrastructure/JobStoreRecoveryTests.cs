using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Checkpoints;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Worker;
using LinqToDB;
using LinqToDB.Data;
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
        await store.MarkFailedAsync(failedClaim.Lease, "NonResumableInterrupted", CancellationToken.None);
        var verifying = store.Start(new(Guid.NewGuid(), "start-verifying")).Job;
        var verifyingClaim = (await store.TryClaimNextAsync("worker-b", ttl, CancellationToken.None))!;
        await store.PrepareAsync(verifyingClaim, CancellationToken.None);
        await store.MarkRunningAsync(verifyingClaim.Lease, CancellationToken.None);
        await store.MarkVerifyingAsync(verifyingClaim.Lease, CancellationToken.None);

        using var db = fixture.Database.Open();
        Assert.Equal(
            "NonResumableInterrupted",
            db.Query<string>(
                    "SELECT FailureCode FROM Jobs WHERE JobId = @jobId",
                    new DataParameter("jobId", failed.JobId.ToString())
                )
                .Single()
        );
        Assert.Equal(JobState.Verifying, await store.GetStateAsync(verifying.JobId, CancellationToken.None));
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
                    new DataParameter("jobId", job.JobId.ToString())
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
                    new DataParameter("jobId", job.ToString()),
                    new DataParameter("runId", run.ToString())
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
                    new DataParameter("jobId", job.ToString()),
                    new DataParameter("runId", run.ToString())
                )
                .Single()
        );
    }

    [Fact]
    public void BatchCheckpointMirrorRow_WhenPersisted_MapsEveryDerivedCheckpointValue()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var job = fixture.SeedJob();
        var row = new BatchCheckpointMirrorRow
        {
            JobId = job.ToString(),
            RunId = Guid.NewGuid().ToString(),
            LastCommittedBatchSequence = 2,
            LastCommittedStableKey = "Id=2",
            CumulativeRowCount = 20,
            SealedManifestHash = "seal",
            FenceToken = 3,
            UpdatedUtc = fixture.Clock.UtcNow.ToString("O"),
        };

        using var db = fixture.Database.Open();
        db.Insert(row);
        var persisted = db.GetTable<BatchCheckpointMirrorRow>().Single();

        Assert.Equal(row.JobId, persisted.JobId);
        Assert.Equal(row.RunId, persisted.RunId);
        Assert.Equal(row.LastCommittedBatchSequence, persisted.LastCommittedBatchSequence);
        Assert.Equal(row.LastCommittedStableKey, persisted.LastCommittedStableKey);
        Assert.Equal(row.CumulativeRowCount, persisted.CumulativeRowCount);
        Assert.Equal(row.SealedManifestHash, persisted.SealedManifestHash);
        Assert.Equal(row.FenceToken, persisted.FenceToken);
        Assert.Equal(row.UpdatedUtc, persisted.UpdatedUtc);
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
