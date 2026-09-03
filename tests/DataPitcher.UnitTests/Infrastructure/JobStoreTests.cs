using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Persistence;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class JobStoreTests
{
    [Fact]
    public void JobStore_WhenStartIsDuplicated_ReturnsTheOriginalQueuedJob()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock);
        var first = store.Start(new(Guid.NewGuid(), "start-42")); var duplicate = store.Start(new(Guid.NewGuid(), "start-42"));
        Assert.True(first.Created); Assert.False(duplicate.Created); Assert.Equal(first.Job.JobId, duplicate.Job.JobId); Assert.Equal(JobState.Queued, first.Job.State);
    }

    [Fact]
    public void JobStore_WhenStarted_ReadsBackEveryPersistedField()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "start-45")).Job;
        var read = store.Get(job.JobId);
        Assert.Equal(job.JobId, read.JobId); Assert.Equal(job.RunId, read.RunId); Assert.Equal(job.PlanId, read.PlanId); Assert.Equal(job.IdempotencyKey, read.IdempotencyKey); Assert.Equal(job.State, read.State);
    }

    [Fact]
    public void JobStore_WhenIdempotencyKeyIsBlank_RejectsTheStart()
    {
        using var fixture = new ControlDatabaseFixture(); var store = new JobStore(fixture.Database, fixture.Clock);

        Assert.Throws<ArgumentException>(() => store.Start(new(Guid.NewGuid(), "")));
    }

    [Fact]
    public void JobStore_WhenLeaseIsCurrent_PersistsTheStateChangeAndItsHistory()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "start-43")).Job; var lease = new LeaseStore(fixture.Database, fixture.Clock).Acquire(job.JobId, "worker-a", TimeSpan.FromMinutes(1))!;
        var result = store.TryTransition(lease, JobState.Preparing);
        Assert.Equal(1, result.RowsAffected); Assert.Equal(JobState.Preparing, result.Job!.State);
        Assert.Equal([(JobState.Draft, JobState.Queued), (JobState.Queued, JobState.Preparing)], store.GetHistory(job.JobId));
    }

    [Fact]
    public async Task JobStore_WhenFirstOwnerIsStale_ItsGuardedWriteAffectsZeroRows()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "start-44")).Job; var leases = new LeaseStore(fixture.Database, fixture.Clock); var ttl = TimeSpan.FromMinutes(1);
        var first = leases.Acquire(job.JobId, "worker-a", ttl)!; var firstReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var takeoverComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleWrite = Task.Run(async () => { firstReady.SetResult(true); await takeoverComplete.Task; return store.TryTransition(first, JobState.Preparing); });
        await firstReady.Task; fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1))); var second = leases.Acquire(job.JobId, "worker-b", ttl)!; takeoverComplete.SetResult(true);
        var stale = await staleWrite;
        Assert.True(second.FenceToken > first.FenceToken); Assert.Equal(0, stale.RowsAffected); Assert.DoesNotContain(store.GetHistory(job.JobId), x => x == (JobState.Queued, JobState.Preparing));
    }

    [Fact]
    public void JobStore_WhenOwnerReacquiresAfterExpiry_RejectsTheStaleFenceEvenWithMatchingOwner()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "start-46")).Job; var leases = new LeaseStore(fixture.Database, fixture.Clock); var ttl = TimeSpan.FromMinutes(1);
        var first = leases.Acquire(job.JobId, "worker-a", ttl)!;
        fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1)));
        var reacquired = leases.Acquire(job.JobId, "worker-a", ttl)!;

        var stale = store.TryTransition(first, JobState.Preparing);

        Assert.Equal(first.OwnerId, reacquired.OwnerId); Assert.True(reacquired.FenceToken > first.FenceToken);
        Assert.Equal(0, stale.RowsAffected); Assert.DoesNotContain(store.GetHistory(job.JobId), x => x == (JobState.Queued, JobState.Preparing));
    }
}
