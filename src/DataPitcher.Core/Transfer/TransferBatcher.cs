using System.Runtime.CompilerServices;
namespace DataPitcher.Core.Transfer;
public sealed class TransferBatcher
{
    private readonly TransferPipelineOptions _options;
    public TransferBatcher(TransferPipelineOptions options) { ArgumentNullException.ThrowIfNull(options); _options = options; }
    public async IAsyncEnumerable<TransferBatch> ReadBatchesAsync(ITransferRowSource source, ITransferRowConverter? converter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); var rows = new List<TransferRow>(); long bytes = 0; long sequence = 1;
        await foreach (var sourceRow in source.ReadAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested(); var row = converter is null ? sourceRow : await converter.ConvertAsync(sourceRow, cancellationToken);
            if (rows.Count > 0 && row.PayloadBytes > _options.BatchTarget.MaximumBytes - bytes)
            { yield return new TransferBatch(sequence++, rows); rows = []; bytes = 0; }
            rows.Add(row); bytes += row.PayloadBytes;
            if (rows.Count == _options.BatchTarget.MaximumRows || bytes >= _options.BatchTarget.MaximumBytes)
            { yield return new TransferBatch(sequence++, rows); rows = []; bytes = 0; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.Count > 0) yield return new TransferBatch(sequence, rows);
    }
}
