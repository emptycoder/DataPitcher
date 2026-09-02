using System.Runtime.CompilerServices;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using Xunit;
namespace DataPitcher.UnitTests.Transfer;
public sealed class TransferAccountingTests
{
    private const int MiB = 1024 * 1024;
    [Fact] public async Task RunAsync_AccountsRowsBytesDurationsAndRates()
    {
        var clock = new ManualClock([At(0), At(0), At(2), At(2), At(5), At(5)]);
        var pipeline = new BoundedTransferPipeline(new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1), clock.Next);
        var result = await pipeline.RunAsync(new Source([R(1), R(2), R(3), R(4)]), null, new AccountingWriter(), CancellationToken.None);
        Assert.Equal(4, result.RowsRead); Assert.Equal(1, result.RowsInserted); Assert.Equal(1, result.RowsUpdated); Assert.Equal(1, result.RowsSkipped); Assert.Equal(1, result.RowsFailed);
        Assert.Equal(16L * MiB, result.BytesRead); Assert.Equal(16L * MiB, result.BytesWritten); Assert.Equal(TimeSpan.FromSeconds(5), result.Duration);
        Assert.Equal(2, result.Batches[0].Batch.Rows.Count); Assert.Equal(1, result.Batches[0].Result.Inserted); Assert.Equal(TimeSpan.FromSeconds(2), result.Batches[0].Duration); Assert.Equal(TimeSpan.FromSeconds(3), result.Batches[1].Duration);
        Assert.Equal(0.8d, result.RowsPerSecond, 10); Assert.Equal(3.2d, result.MebibytesPerSecond, 10);
    }
    [Fact] public async Task RunAsync_WhenWriterDoesNotAccountForEveryRow_RejectsTheResult()
    {
        var pipeline = new BoundedTransferPipeline(new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1), () => At(0));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.RunAsync(new Source([R(1), R(2)]), null, new IncorrectWriter(), CancellationToken.None));
        Assert.Equal("Writer result must account for every row in batch 1.", exception.Message);
    }
    [Fact] public void TransferPipelineResult_WhenDurationIsZero_ReportsZeroRates()
    {
        var result = new TransferPipelineResult(1, 1, [], TimeSpan.Zero);
        Assert.Equal(0, result.RowsPerSecond); Assert.Equal(0, result.MebibytesPerSecond); Assert.Empty(result.Batches);
    }
    private static TransferRow R(int value) => new([value], 4L * MiB);
    private static DateTimeOffset At(int seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);
    private sealed class Source(IReadOnlyList<TransferRow> rows) : ITransferRowSource
    { public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken) { foreach (var row in rows) { cancellationToken.ThrowIfCancellationRequested(); yield return row; await Task.CompletedTask; } } }
    private sealed class AccountingWriter : ITransferBatchWriter
    { public Task<BatchWriteResult> WriteAsync(TransferBatch batch, CancellationToken cancellationToken) => Task.FromResult(batch.Sequence == 1 ? new BatchWriteResult(1, 1, 0, 0, batch.PayloadBytes) : new BatchWriteResult(0, 0, 1, 1, batch.PayloadBytes)); }
    private sealed class IncorrectWriter : ITransferBatchWriter
    { public Task<BatchWriteResult> WriteAsync(TransferBatch batch, CancellationToken cancellationToken) => Task.FromResult(new BatchWriteResult(1, 0, 0, 0, batch.PayloadBytes)); }
    private sealed class ManualClock(IEnumerable<DateTimeOffset> values)
    { private readonly Queue<DateTimeOffset> _values = new Queue<DateTimeOffset>(values); public DateTimeOffset Next() => _values.Dequeue(); }
}
