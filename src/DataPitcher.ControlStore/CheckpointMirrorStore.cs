using System.Globalization;
using DataPitcher.Core.Transfer;

namespace DataPitcher.ControlStore;

public sealed class CheckpointMirrorStore(ControlDatabase database) : IControlCheckpointMirror
{
    public async Task OverwriteAsync(TargetCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        using var db = database.OpenNative();
        await db.ExecuteAsync(
            "INSERT INTO BatchCheckpointMirrors (JobId, RunId, LastCommittedBatchSequence, LastCommittedStableKey, CumulativeRowCount, SealedManifestHash, FenceToken, UpdatedUtc) VALUES (@job, @run, @batch, @key, @rows, @seal, @fence, @updated) ON CONFLICT(JobId, RunId) DO UPDATE SET LastCommittedBatchSequence = excluded.LastCommittedBatchSequence, LastCommittedStableKey = excluded.LastCommittedStableKey, CumulativeRowCount = excluded.CumulativeRowCount, SealedManifestHash = excluded.SealedManifestHash, FenceToken = excluded.FenceToken, UpdatedUtc = excluded.UpdatedUtc",
            cancellationToken,
            new ControlParameter("job", checkpoint.JobId.ToString()),
            new ControlParameter("run", checkpoint.RunId.ToString()),
            new ControlParameter("batch", checkpoint.BatchSequence),
            new ControlParameter("key", checkpoint.LastStableKey?.ToString()),
            new ControlParameter("rows", checkpoint.RowCount),
            new ControlParameter("seal", checkpoint.ManifestSealHash),
            new ControlParameter("fence", checkpoint.FenceToken),
            new ControlParameter("updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
        );
    }
}
