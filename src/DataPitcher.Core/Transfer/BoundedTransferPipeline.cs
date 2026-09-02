using System.Runtime.CompilerServices;
using System.Threading.Channels;
namespace DataPitcher.Core.Transfer;
public sealed class BoundedTransferPipeline
{
    private readonly TransferBatcher _batcher; private readonly Func<DateTimeOffset> _utcNow; private readonly Action<TransferBatch>? _beforeQueueWrite;
    public BoundedTransferPipeline(TransferPipelineOptions options, Func<DateTimeOffset> utcNow) : this(options, utcNow, null) { }
    internal BoundedTransferPipeline(TransferPipelineOptions options, Func<DateTimeOffset> utcNow, Action<TransferBatch>? beforeQueueWrite)
    { ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(utcNow); _batcher = new TransferBatcher(options); _utcNow = utcNow; _beforeQueueWrite = beforeQueueWrite; MaximumQueuedBatches = options.MaximumQueuedBatches; }
    internal int MaximumQueuedBatches { get; }
    public async Task<TransferPipelineResult> RunAsync(ITransferRowSource source, ITransferRowConverter? converter, ITransferBatchWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(writer); var startedAt = _utcNow(); long rowsRead = 0; long bytesRead = 0; var batches = new List<BatchTransferStatistics>();
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<TransferBatch>(new BoundedChannelOptions(MaximumQueuedBatches) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var producer = ProduceAsync(channel.Writer, new CountingSource(source, row => { rowsRead++; bytesRead += row.PayloadBytes; }), converter, stopped.Token);
        try { await ConsumeAsync(channel.Reader, writer, batches, stopped.Token); await producer; return new TransferPipelineResult(rowsRead, bytesRead, batches, _utcNow() - startedAt); }
        catch { stopped.Cancel(); try { await producer; } catch { } throw; }
    }
    private async Task ProduceAsync(ChannelWriter<TransferBatch> writer, ITransferRowSource source, ITransferRowConverter? converter, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try { await foreach (var batch in _batcher.ReadBatchesAsync(source, converter, cancellationToken)) { _beforeQueueWrite?.Invoke(batch); await writer.WriteAsync(batch, cancellationToken); } }
        catch (Exception exception) { failure = exception; throw; }
        finally { writer.TryComplete(failure); }
    }
    private async Task ConsumeAsync(ChannelReader<TransferBatch> reader, ITransferBatchWriter writer, ICollection<BatchTransferStatistics> batches, CancellationToken cancellationToken)
    {
        await foreach (var batch in reader.ReadAllAsync(cancellationToken))
        {
            var startedAt = _utcNow(); var result = await writer.WriteAsync(batch, cancellationToken); var finishedAt = _utcNow();
            if (result.Inserted + result.Updated + result.Skipped + result.Failed != batch.Rows.Count) throw new InvalidOperationException($"Writer result must account for every row in batch {batch.Sequence}.");
            batches.Add(new BatchTransferStatistics(batch, result, finishedAt - startedAt));
        }
    }
    private sealed class CountingSource(ITransferRowSource source, Action<TransferRow> rowRead) : ITransferRowSource
    {
        public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { await foreach (var row in source.ReadAsync(cancellationToken).WithCancellation(cancellationToken)) { rowRead(row); yield return row; } }
    }
}
