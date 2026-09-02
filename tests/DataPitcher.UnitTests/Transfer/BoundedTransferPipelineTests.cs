using System.Runtime.CompilerServices;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using Xunit;
namespace DataPitcher.UnitTests.Transfer;
public sealed class BoundedTransferPipelineTests
{
    private const int MiB = 1024 * 1024;
    [Fact] public async Task RunAsync_WhenWriterIsBlocked_ThrottlesFastSourceAtFixedRowBound()
    {
        var options = new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1); var source = new CountingSource(8); var writer = new GateWriter(); var thirdEnqueue = new TaskCompletionSource<TransferBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new BoundedTransferPipeline(options, () => DateTimeOffset.UnixEpoch, batch => { if (batch.Sequence == 3) thirdEnqueue.TrySetResult(batch); });
        var run = pipeline.RunAsync(source, null, writer, CancellationToken.None);
        await writer.FirstWriteStarted.Task; await thirdEnqueue.Task;
        Assert.Equal(6, source.RowsRead);
        Assert.InRange(source.RowsRead, 0, (options.MaximumQueuedBatches + 2) * options.BatchTarget.MaximumRows);
        writer.ReleaseFirstWrite(); await run; Assert.Equal([1L, 2L, 3L, 4L], writer.Batches.Select(batch => batch.Sequence));
    }
    [Fact] public async Task RunAsync_WhenCancelledWithAnIncompleteBatch_DoesNotWriteThatPartialBatch()
    {
        using var cancellation = new CancellationTokenSource(); var source = new PausingSource(); var converter = new RecordingConverter(); var writer = new GateWriter();
        var run = new BoundedTransferPipeline(new TransferPipelineOptions(new BatchTarget(4, 8 * MiB), 1), () => DateTimeOffset.UnixEpoch).RunAsync(source, converter, writer, cancellation.Token);
        await source.FirstRowRead.Task; cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(source.ReceivedCancelableToken); Assert.True(converter.ReceivedCancelableToken); Assert.Empty(writer.Batches);
    }
    [Fact] public async Task RunAsync_WhenCancelledDuringWriter_PropagatesCancellationToEveryActiveStage()
    {
        using var cancellation = new CancellationTokenSource(); var source = new CountingSource(2); var converter = new RecordingConverter(); var writer = new GateWriter();
        var run = new BoundedTransferPipeline(new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1), () => DateTimeOffset.UnixEpoch).RunAsync(source, converter, writer, cancellation.Token);
        await writer.FirstWriteStarted.Task; cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(source.ReceivedCancelableToken); Assert.True(converter.ReceivedCancelableToken); Assert.True(writer.ReceivedCancelableToken); Assert.True(writer.ObservedCancellation);
    }
    private sealed class CountingSource(int count) : ITransferRowSource
    {
        public int RowsRead { get; private set; } public bool ReceivedCancelableToken { get; private set; }
        public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { ReceivedCancelableToken = cancellationToken.CanBeCanceled; for (var value = 1; value <= count; value++) { cancellationToken.ThrowIfCancellationRequested(); RowsRead++; yield return new TransferRow([value], 1); await Task.CompletedTask; } }
    }
    private sealed class PausingSource : ITransferRowSource
    {
        public TaskCompletionSource<bool> FirstRowRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool ReceivedCancelableToken { get; private set; }
        public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { ReceivedCancelableToken = cancellationToken.CanBeCanceled; FirstRowRead.TrySetResult(true); yield return new TransferRow([1], 1); await new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken); }
    }
    private sealed class RecordingConverter : ITransferRowConverter
    { public bool ReceivedCancelableToken { get; private set; } public ValueTask<TransferRow> ConvertAsync(TransferRow row, CancellationToken cancellationToken) { ReceivedCancelableToken = cancellationToken.CanBeCanceled; return ValueTask.FromResult(row); } }
    private sealed class GateWriter : ITransferBatchWriter
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously); public List<TransferBatch> Batches { get; } = []; public TaskCompletionSource<bool> FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool ReceivedCancelableToken { get; private set; } public bool ObservedCancellation { get; private set; }
        public async Task<BatchWriteResult> WriteAsync(TransferBatch batch, CancellationToken cancellationToken)
        { Batches.Add(batch); ReceivedCancelableToken = cancellationToken.CanBeCanceled; if (batch.Sequence == 1) { FirstWriteStarted.TrySetResult(true); try { await _release.Task.WaitAsync(cancellationToken); } catch (OperationCanceledException) { ObservedCancellation = true; throw; } } return new(batch.Rows.Count, 0, 0, 0, batch.PayloadBytes); }
        public void ReleaseFirstWrite() => _release.TrySetResult(true);
    }
}
