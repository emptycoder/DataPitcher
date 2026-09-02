using System.Globalization;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Worker;
using LinqToDB.Async;
using LinqToDB.Data;

namespace DataPitcher.Infrastructure.Checkpoints;

public sealed class CheckpointMirrorStore(ControlDatabase database) : IControlCheckpointMirror
{
    public async Task OverwriteAsync(TargetCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        using var db = database.Open();
        await db.ExecuteAsync("INSERT INTO BatchCheckpointMirrors (JobId, RunId, LastCommittedBatchSequence, LastCommittedStableKey, CumulativeRowCount, SealedManifestHash, FenceToken, UpdatedUtc) VALUES (@job, @run, @batch, @key, @rows, @seal, @fence, @updated) ON CONFLICT(JobId, RunId) DO UPDATE SET LastCommittedBatchSequence = excluded.LastCommittedBatchSequence, LastCommittedStableKey = excluded.LastCommittedStableKey, CumulativeRowCount = excluded.CumulativeRowCount, SealedManifestHash = excluded.SealedManifestHash, FenceToken = excluded.FenceToken, UpdatedUtc = excluded.UpdatedUtc", cancellationToken, new DataParameter[] { new("job", checkpoint.JobId.ToString()), new("run", checkpoint.RunId.ToString()), new("batch", checkpoint.BatchSequence), new("key", checkpoint.LastStableKey?.ToString()), new("rows", checkpoint.RowCount), new("seal", checkpoint.ManifestSealHash), new("fence", checkpoint.FenceToken), new("updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)) });
    }
}
