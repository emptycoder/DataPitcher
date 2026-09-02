using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Worker;
using Xunit;

namespace DataPitcher.UnitTests.Worker;

public sealed class RecoveryCoordinatorTests
{
    private static (JobClaim Claim, TransferRun Run) NewClaim(bool isInterrupted, bool supportsDurableResume, string sealHash)
    {
        var jobId = Guid.NewGuid(); var runId = Guid.NewGuid();
        var job = new TransferJob(jobId, runId, Guid.NewGuid(), "key", JobState.Running);
        var lease = new LeaseGrant(jobId, "worker-a", 3, DateTimeOffset.UnixEpoch.AddMinutes(1), DateTimeOffset.UnixEpoch);
        return (new JobClaim(job, lease, isInterrupted), new TransferRun(jobId, runId, sealHash, supportsDurableResume));
    }

    [Fact]
    public async Task RecoverAsync_WhenCheckpointIsCurrent_ReadsTargetFenceBeforeOverwritingTheMirrorWithTheRecoveredKey()
    {
        var (claim, run) = NewClaim(isInterrupted: false, supportsDurableResume: true, sealHash: "seal");
        var key700 = new StableKey([new KeyComponent("Id", 700)]);
        var checkpoint = new TargetCheckpoint(run.JobId, run.RunId, 7, key700, 700, "seal", claim.Lease.FenceToken);
        var calls = new List<string>();
        var target = new TestTargetRunSession(checkpoint, "target-connection", calls) { Snapshot = new(checkpoint, []) };
        var mirror = new RecordingCheckpointMirror(calls);
        var coordinator = new RecoveryCoordinator(mirror);

        var result = await coordinator.RecoverAsync(claim, run, target, CancellationToken.None);

        Assert.Equal(checkpoint, result);
        Assert.Equal(["Fence", "Repair", "Mirror"], calls);
        Assert.Same(claim.Lease, target.LastFenceLease);
        Assert.Single(mirror.Checkpoints, item => item == checkpoint);
    }

    [Fact]
    public async Task RecoverAsync_WhenTargetSealHashDiffersFromTheRun_ThrowsWithoutOverwritingTheMirror()
    {
        var (claim, run) = NewClaim(isInterrupted: true, supportsDurableResume: true, sealHash: "current-seal");
        var checkpoint = new TargetCheckpoint(run.JobId, run.RunId, 4, null, 400, "stale-seal", claim.Lease.FenceToken);
        var calls = new List<string>();
        var target = new TestTargetRunSession(checkpoint, "target-connection", calls) { Snapshot = new(checkpoint, []) };
        var mirror = new RecordingCheckpointMirror(calls);
        var coordinator = new RecoveryCoordinator(mirror);

        await Assert.ThrowsAsync<ManifestSealMismatchException>(() => coordinator.RecoverAsync(claim, run, target, CancellationToken.None));

        Assert.Empty(mirror.Checkpoints);
    }

    [Fact]
    public async Task RecoverAsync_WhenInterruptedRunDoesNotSupportDurableResume_ThrowsWithoutContactingTheTarget()
    {
        var (claim, run) = NewClaim(isInterrupted: true, supportsDurableResume: false, sealHash: "seal");
        var checkpoint = new TargetCheckpoint(run.JobId, run.RunId, 1, null, 1, "seal", claim.Lease.FenceToken);
        var calls = new List<string>();
        var target = new TestTargetRunSession(checkpoint, "target-connection", calls) { Snapshot = new(checkpoint, []) };
        var mirror = new RecordingCheckpointMirror(calls);
        var coordinator = new RecoveryCoordinator(mirror);

        await Assert.ThrowsAsync<NonResumableInterruptedException>(() => coordinator.RecoverAsync(claim, run, target, CancellationToken.None));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task RecoverAsync_WhenRepairLeavesAnEntryQuarantined_QuarantinesOnlyThatMutation()
    {
        var (claim, run) = NewClaim(isInterrupted: false, supportsDurableResume: true, sealHash: "seal");
        var checkpoint = new TargetCheckpoint(run.JobId, run.RunId, 2, null, 2, "seal", claim.Lease.FenceToken);
        var repairedTrigger = new TargetMutation("dbo.Orders", "TR_Orders", TargetMutationKind.DisabledTrigger);
        var unrepairedConstraint = new TargetMutation("dbo.Orders", "FK_Orders_Customers", TargetMutationKind.UntrustedConstraint);
        var pendingEntries = new List<MutationJournalEntry>
        {
            new(Guid.NewGuid(), repairedTrigger, MutationJournalState.PendingRepair, null),
            new(Guid.NewGuid(), unrepairedConstraint, MutationJournalState.PendingRepair, null),
        };
        var repairResult = new List<MutationJournalEntry>
        {
            pendingEntries[0] with { State = MutationJournalState.Repaired },
            pendingEntries[1] with { State = MutationJournalState.Quarantined, Detail = "Constraint could not be verified trusted." },
        };
        var calls = new List<string>();
        var target = new TestTargetRunSession(checkpoint, "target-connection", calls) { Snapshot = new(checkpoint, pendingEntries), RepairResult = repairResult };
        var mirror = new RecordingCheckpointMirror(calls);
        var coordinator = new RecoveryCoordinator(mirror);

        await coordinator.RecoverAsync(claim, run, target, CancellationToken.None);

        Assert.Single(target.QuarantinedMutations, mutation => mutation == unrepairedConstraint);
        Assert.DoesNotContain(target.QuarantinedMutations, mutation => mutation == repairedTrigger);
    }
}
