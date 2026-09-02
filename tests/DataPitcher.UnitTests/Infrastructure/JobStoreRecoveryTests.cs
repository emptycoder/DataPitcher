using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Checkpoints;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Worker;
using LinqToDB.Data;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class JobStoreRecoveryTests
{
    [Fact]
    public async Task JobStore_WhenDuplicateStartsRace_ClaimsTheJobOnlyOnce()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock); var request = new StartJobRequest(Guid.NewGuid(), "start-recovery-race");
        var releaseStarts = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var startTasks = new[] { StartAfterAsync(releaseStarts.Task, store, request), StartAfterAsync(releaseStarts.Task, store, request) };
        releaseStarts.SetResult(true);
        var starts = await Task.WhenAll(startTasks);

        var releaseClaims = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var claimTasks = new[] { ClaimAfterAsync(releaseClaims.Task, store), ClaimAfterAsync(releaseClaims.Task, store) };
        releaseClaims.SetResult(true);
        var claims = await Task.WhenAll(claimTasks);

        Assert.Single(starts.Select(result => result.Job.JobId).Distinct());
        Assert.Single(claims, claim => claim?.Job.JobId == starts[0].Job.JobId);
    }

    [Fact]
    public async Task JobStore_WhenActiveLeaseExpires_ReclaimsItAsInterrupted()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock); var job = store.Start(new(Guid.NewGuid(), "start-interrupted")).Job; var ttl = TimeSpan.FromMinutes(1);
        var first = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(first, CancellationToken.None); await store.MarkRunningAsync(first.Lease, CancellationToken.None);

        Assert.Null(await store.TryClaimNextAsync("worker-b", ttl, CancellationToken.None));
        fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1)));
        var recovered = await store.TryClaimNextAsync("worker-b", ttl, CancellationToken.None);

        Assert.NotNull(recovered); Assert.True(recovered!.IsInterrupted); Assert.Equal(job.JobId, recovered.Job.JobId);
    }

    [Fact]
    public async Task JobStore_WhenOperatorRecordsControlIntent_DoesNotRequireAWorkerLease()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock); var ttl = TimeSpan.FromMinutes(1); var job = store.Start(new(Guid.NewGuid(), "start-controls")).Job;
        var claim = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(claim, CancellationToken.None); await store.MarkRunningAsync(claim.Lease, CancellationToken.None); await store.RequestPauseAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobState.Pausing, await store.GetStateAsync(job.JobId, CancellationToken.None));
        await store.MarkPausedAsync(claim.Lease, CancellationToken.None); await store.RequestResumeAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobState.Queued, await store.GetStateAsync(job.JobId, CancellationToken.None));

        var resumed = (await store.TryClaimNextAsync("worker-b", ttl, CancellationToken.None))!;
        await store.PrepareAsync(resumed, CancellationToken.None); await store.MarkRunningAsync(resumed.Lease, CancellationToken.None); await store.RequestCancelAsync(job.JobId, CancellationToken.None); await store.MarkCancelledAsync(resumed.Lease, CancellationToken.None);

        Assert.Equal(JobState.Cancelled, await store.GetStateAsync(job.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task JobStore_WhenWorkerFailsOrVerifies_PersistsTheGuardedTransitionAndFailureCode()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock); var ttl = TimeSpan.FromMinutes(1); var failed = store.Start(new(Guid.NewGuid(), "start-failed")).Job;
        var failedClaim = (await store.TryClaimNextAsync("worker-a", ttl, CancellationToken.None))!;
        await store.PrepareAsync(failedClaim, CancellationToken.None); await store.MarkRunningAsync(failedClaim.Lease, CancellationToken.None); await store.MarkFailedAsync(failedClaim.Lease, "NonResumableInterrupted", CancellationToken.None);
        var verifying = store.Start(new(Guid.NewGuid(), "start-verifying")).Job;
        var verifyingClaim = (await store.TryClaimNextAsync("worker-b", ttl, CancellationToken.None))!;
        await store.PrepareAsync(verifyingClaim, CancellationToken.None); await store.MarkRunningAsync(verifyingClaim.Lease, CancellationToken.None); await store.MarkVerifyingAsync(verifyingClaim.Lease, CancellationToken.None);

        using var db = fixture.Database.Open();
        Assert.Equal("NonResumableInterrupted", db.Query<string>("SELECT FailureCode FROM Jobs WHERE JobId = @jobId", new DataParameter("jobId", failed.JobId.ToString())).Single());
        Assert.Equal(JobState.Verifying, await store.GetStateAsync(verifying.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task CheckpointMirrorStore_WhenTargetCheckpointAdvances_ReplacesTheDisplayCopy()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var job = fixture.SeedJob(); var run = Guid.NewGuid(); var mirror = new CheckpointMirrorStore(fixture.Database);
        await mirror.OverwriteAsync(new(job, run, 1, new StableKey([new KeyComponent("Id", 1)]), 10, "seal", 1), CancellationToken.None);
        await mirror.OverwriteAsync(new(job, run, 2, new StableKey([new KeyComponent("Id", 2)]), 20, "seal", 2), CancellationToken.None);

        using var db = fixture.Database.Open();
        Assert.Equal(2, db.Query<long>("SELECT LastCommittedBatchSequence FROM BatchCheckpointMirrors WHERE JobId = @jobId AND RunId = @runId", new DataParameter("jobId", job.ToString()), new DataParameter("runId", run.ToString())).Single());
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
