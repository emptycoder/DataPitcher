using DataPitcher.Infrastructure.Leasing;

namespace DataPitcher.Infrastructure.Worker;

public sealed class RecoveryCoordinator(IControlCheckpointMirror mirror)
{
    public async Task<TargetCheckpoint> RecoverAsync(
        JobClaim claim,
        TransferRun run,
        ITargetRunSession target,
        CancellationToken cancellationToken
    )
    {
        if (claim.IsInterrupted && !run.SupportsDurableResume)
            throw new NonResumableInterruptedException();

        var snapshot = await target.AcquireFenceReadCheckpointAndJournalAsync(run, claim.Lease, cancellationToken);
        if (!StringComparer.Ordinal.Equals(snapshot.Checkpoint.ManifestSealHash, run.ManifestSealHash))
            throw new ManifestSealMismatchException();

        var repaired = await target.RepairMutationsAsync(snapshot.Mutations, cancellationToken);
        foreach (var entry in repaired.Where(entry => entry.State is MutationJournalState.Quarantined))
            await target.QuarantineAsync(
                entry.Mutation,
                entry.Detail ?? "Target mutation repair could not be verified.",
                cancellationToken
            );

        await mirror.OverwriteAsync(snapshot.Checkpoint, cancellationToken);
        return snapshot.Checkpoint;
    }
}
