namespace DataPitcher.Core.Plans;

/// <summary>
/// The plan was sealed by an older sealing algorithm. Its table order, manifest or warnings may not be what the
/// current transfer code expects, so the operator has to seal it again before a job can start.
/// </summary>
public sealed class StalePlanException(int sealingVersion)
    : InvalidOperationException(
        $"This plan was sealed by an older version of DataPitcher (sealing version {sealingVersion}, current {TransferPlanContent.CurrentSealingVersion}). Seal the plan again before starting a transfer."
    )
{
    public int SealingVersion { get; } = sealingVersion;
}
