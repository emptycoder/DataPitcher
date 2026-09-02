using System.Threading.Channels;
namespace DataPitcher.Core.Transfer;
public sealed class BoundedTransferPipeline
{
    private readonly TransferBatcher _batcher; private readonly Action<TransferBatch>? _beforeQueueWrite;
    public BoundedTransferPipeline(TransferPipelineOptions options, Func<DateTimeOffset> utcNow) : this(options, utcNow, null) { }
    internal BoundedTransferPipeline(TransferPipelineOptions options, Func<DateTimeOffset> utcNow, Action<TransferBatch>? beforeQueueWrite)
    { ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(utcNow); _batcher = new TransferBatcher(options); _beforeQueueWrite = beforeQueueWrite; MaximumQueuedBatches = options.MaximumQueuedBatches; }
    internal int MaximumQueuedBatches { get; }
    public async Task RunAsync(ITransferRowSource source, ITransferRowConverter? converter, ITransferBatchWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(writer);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<TransferBatch>(new BoundedChannelOptions(MaximumQueuedBatches) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var producer = ProduceAsync(channel.Writer, source, converter, stopped.Token);
        try { await ConsumeAsync(channel.Reader, writer, stopped.Token); await producer; }
        catch { stopped.Cancel(); try { await producer; } catch { } throw; }
    }
    private async Task ProduceAsync(ChannelWriter<TransferBatch> writer, ITransferRowSource source, ITransferRowConverter? converter, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try { await foreach (var batch in _batcher.ReadBatchesAsync(source, converter, cancellationToken)) { _beforeQueueWrite?.Invoke(batch); await writer.WriteAsync(batch, cancellationToken); } }
        catch (Exception exception) { failure = exception; throw; }
        finally { writer.TryComplete(failure); }
    }
    private static async Task ConsumeAsync(ChannelReader<TransferBatch> reader, ITransferBatchWriter writer, CancellationToken cancellationToken)
    { await foreach (var batch in reader.ReadAllAsync(cancellationToken)) await writer.WriteAsync(batch, cancellationToken); }
}
