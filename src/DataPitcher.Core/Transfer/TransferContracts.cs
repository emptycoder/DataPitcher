using DataPitcher.Core.Plans;

namespace DataPitcher.Core.Transfer;

public sealed class TransferRow
{
    public TransferRow(IEnumerable<object?> values, long payloadBytes)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBytes);
        Values = Array.AsReadOnly(values.ToArray());
        PayloadBytes = payloadBytes;
    }

    public IReadOnlyList<object?> Values { get; }
    public long PayloadBytes { get; }
}

public sealed class TransferBatch
{
    public TransferBatch(long sequence, IEnumerable<TransferRow> rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentNullException.ThrowIfNull(rows);
        var copiedRows = rows.ToArray();
        if (copiedRows.Length == 0)
            throw new ArgumentException("A transfer batch must contain at least one row.", nameof(rows));
        Sequence = sequence;
        Rows = Array.AsReadOnly(copiedRows);
        PayloadBytes = copiedRows.Sum(row => row.PayloadBytes);
    }

    public long Sequence { get; }
    public IReadOnlyList<TransferRow> Rows { get; }
    public long PayloadBytes { get; }
}

public sealed record BatchWriteResult(long Inserted, long Updated, long Skipped, long Failed, long BytesWritten);

public interface ITransferRowSource
{
    IAsyncEnumerable<TransferRow> ReadAsync(CancellationToken cancellationToken);
}

public interface ITransferRowConverter
{
    ValueTask<TransferRow> ConvertAsync(TransferRow row, CancellationToken cancellationToken);
}

public interface ITransferBatchWriter
{
    Task<BatchWriteResult> WriteAsync(TransferBatch batch, CancellationToken cancellationToken);
}

public sealed class TransferPipelineOptions
{
    private const int MinimumPayloadBytes = 8 * 1024 * 1024;
    private const int MaximumPayloadBytes = 32 * 1024 * 1024;

    public TransferPipelineOptions(BatchTarget batchTarget, int maximumQueuedBatches)
    {
        ArgumentNullException.ThrowIfNull(batchTarget);
        if (batchTarget.MaximumRows < 1)
            throw new ArgumentOutOfRangeException(nameof(batchTarget), "Maximum rows must be positive.");
        if (batchTarget.MaximumBytes < MinimumPayloadBytes || batchTarget.MaximumBytes > MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(
                nameof(batchTarget),
                "Target payload must be between 8 MiB and 32 MiB."
            );
        if (maximumQueuedBatches is < 1 or > 2)
            throw new ArgumentOutOfRangeException(
                nameof(maximumQueuedBatches),
                "The queue must contain one or two batches."
            );
        BatchTarget = batchTarget;
        MaximumQueuedBatches = maximumQueuedBatches;
    }

    public BatchTarget BatchTarget { get; }
    public int MaximumQueuedBatches { get; }
}

public sealed class BatchTransferStatistics
{
    public BatchTransferStatistics(TransferBatch batch, BatchWriteResult result, TimeSpan duration)
    {
        Batch = batch;
        Result = result;
        Duration = duration;
    }

    public TransferBatch Batch { get; }
    public BatchWriteResult Result { get; }
    public TimeSpan Duration { get; }
}

public sealed class TransferPipelineResult
{
    public TransferPipelineResult(
        long rowsRead,
        long bytesRead,
        IEnumerable<BatchTransferStatistics> batches,
        TimeSpan duration
    )
    {
        ArgumentNullException.ThrowIfNull(batches);
        RowsRead = rowsRead;
        BytesRead = bytesRead;
        Batches = Array.AsReadOnly(batches.ToArray());
        Duration = duration;
        RowsInserted = Batches.Sum(batch => batch.Result.Inserted);
        RowsUpdated = Batches.Sum(batch => batch.Result.Updated);
        RowsSkipped = Batches.Sum(batch => batch.Result.Skipped);
        RowsFailed = Batches.Sum(batch => batch.Result.Failed);
        BytesWritten = Batches.Sum(batch => batch.Result.BytesWritten);
    }

    public long RowsRead { get; }
    public long RowsInserted { get; }
    public long RowsUpdated { get; }
    public long RowsSkipped { get; }
    public long RowsFailed { get; }
    public long BytesRead { get; }
    public long BytesWritten { get; }
    public IReadOnlyList<BatchTransferStatistics> Batches { get; }
    public TimeSpan Duration { get; }
    public double RowsPerSecond => Duration <= TimeSpan.Zero ? 0 : RowsRead / Duration.TotalSeconds;
    public double MebibytesPerSecond =>
        Duration <= TimeSpan.Zero ? 0 : BytesWritten / 1024d / 1024d / Duration.TotalSeconds;
}
