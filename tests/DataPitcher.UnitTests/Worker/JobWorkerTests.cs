using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Worker;
using DataPitcher.UnitTests.Infrastructure;
using LinqToDB.Data;
using Xunit;

namespace DataPitcher.UnitTests.Worker;

public sealed class JobWorkerTests
{
    [Fact]
    public async Task LeaseRenewer_WhenClockReachesRenewalDue_RenewsWithoutChangingTheFence()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var jobId = fixture.SeedJob(); var ttl = TimeSpan.FromMinutes(1); var leases = new LeaseStore(fixture.Database, fixture.Clock); var lease = leases.Acquire(jobId, "worker-a", ttl)!;
        var delay = new GateWorkerDelay(); var renewer = new LeaseRenewer(leases, delay); using var leaseLost = new CancellationTokenSource(); using var stop = new CancellationTokenSource();
        var renewing = renewer.RunAsync(lease, ttl, leaseLost, stop.Token);

        Assert.Equal(lease.RenewAfterUtc, await delay.FirstDue);
        fixture.Clock.Advance(lease.RenewAfterUtc - fixture.Clock.UtcNow);
        delay.Release();
        await delay.ReturnedToWait;

        using var db = fixture.Database.Open();
        Assert.Equal(lease.FenceToken, db.Query<long>("SELECT FenceToken FROM JobLeases WHERE JobId = @jobId", new DataParameter("jobId", jobId.ToString())).Single());
        Assert.Equal(fixture.Clock.UtcNow.Add(ttl).ToString("O"), db.Query<string>("SELECT ExpiresUtc FROM JobLeases WHERE JobId = @jobId", new DataParameter("jobId", jobId.ToString())).Single());
        stop.Cancel();
        await renewing;
        Assert.False(leaseLost.IsCancellationRequested);
    }

    [Fact]
    public async Task LeaseRenewer_WhenRenewalIsRejected_CancelsTheLeaseLostToken()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var jobId = fixture.SeedJob(); var ttl = TimeSpan.FromMinutes(1); var leases = new LeaseStore(fixture.Database, fixture.Clock); var lease = leases.Acquire(jobId, "worker-a", ttl)!;
        var delay = new GateWorkerDelay(); var renewer = new LeaseRenewer(leases, delay); using var leaseLost = new CancellationTokenSource(); using var stop = new CancellationTokenSource();
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
        var lease = new LeaseGrant(Guid.NewGuid(), "worker-a", 1, DateTimeOffset.UnixEpoch.AddMinutes(1), DateTimeOffset.UnixEpoch);
        var renewer = new LeaseRenewer(new LeaseStore(null!, null!), new UnreachableWorkerDelay());
        using var leaseLost = new CancellationTokenSource(); using var stop = new CancellationTokenSource(); stop.Cancel();

        await renewer.RunAsync(lease, TimeSpan.FromMinutes(1), leaseLost, stop.Token);

        Assert.False(leaseLost.IsCancellationRequested);
    }

    [Fact]
    public async Task JobWorker_WhenStartTokenIsAlreadyCancelled_NeverClaimsAJob()
    {
        var worker = new JobWorker(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, "worker-a", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource(); cts.Cancel();

        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task JobWorker_WhenNoJobIsQueued_WaitsForTheNextPollInterval()
    {
        using var fixture = new ControlDatabaseFixture(); var pollInterval = TimeSpan.FromMinutes(1); var delay = new GateWorkerDelay();
        var worker = new JobWorker(new NeverClaimsJobControl(), null!, null!, null!, null!, null!, null!, null!, delay, fixture.Clock, "worker-a", TimeSpan.FromMinutes(1), pollInterval);

        await worker.StartAsync(CancellationToken.None);
        var due = await delay.FirstDue;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(fixture.Clock.UtcNow.Add(pollInterval), due);
    }

    [Fact]
    public async Task JobWorker_WhenClaimRunsNormally_MirrorsTheTargetCheckpointAndDisposesItsSessions()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var jobId = fixture.SeedJob(); var runId = Guid.NewGuid(); var ttl = TimeSpan.FromMinutes(1); var lease = new LeaseGrant(jobId, "worker-a", 1, fixture.Clock.UtcNow.Add(ttl), fixture.Clock.UtcNow.AddMinutes(1));
        var job = new TransferJob(jobId, runId, Guid.NewGuid(), "normal-run", JobState.Queued); var run = new TransferRun(jobId, runId, "seal", true); var unit = new TransferUnit(1, new StableKey([new KeyComponent("Id", 1)]), 1, TransferUnitKind.Batch); var checkpoint = new TargetCheckpoint(jobId, runId, 1, unit.LastStableKey, 1, "seal", lease.FenceToken);
        var recoveredKey = new StableKey([new KeyComponent("Id", 700)]); var recoveredCheckpoint = new TargetCheckpoint(jobId, runId, 7, recoveredKey, 700, "seal", lease.FenceToken);
        var calls = new List<string>(); var jobs = new SingleClaimJobControl(new JobClaim(job, lease, false), calls); var source = new TestTransferReadSession(unit, "source-connection"); var sourceFactory = new TestTransferReadSessionFactory(source); var target = new TestTargetRunSession(checkpoint, "target-connection", calls) { Snapshot = new(recoveredCheckpoint, []) }; var mirror = new RecordingCheckpointMirror(calls);
        var worker = new JobWorker(jobs, new TestJobRunCatalog(run), new TestTargetRunSessionFactory(target), sourceFactory, new RecoveryCoordinator(mirror), new LeaseRenewer(new LeaseStore(fixture.Database, fixture.Clock), new BlockingWorkerDelay()), mirror, new NoWorkerFaults(), new BlockingWorkerDelay(), fixture.Clock, "worker-a", ttl, TimeSpan.FromMinutes(1));

        await worker.StartAsync(CancellationToken.None);
        await jobs.MarkedVerifying;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(["Prepare", "Fence", "Repair", "Mirror", "Running", "Apply", "Mirror", "Verifying"], calls); Assert.Equal(recoveredKey, sourceFactory.LastRequestedStartAfter); Assert.Contains(mirror.Checkpoints, item => item == recoveredCheckpoint); Assert.Contains(mirror.Checkpoints, item => item == checkpoint); Assert.True(source.Disposed); Assert.True(target.Disposed); Assert.NotEqual(source.ConnectionOwnerId, target.ConnectionOwnerId);
    }
}
