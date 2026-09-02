using System.Runtime.CompilerServices;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using FsCheck.Xunit;
using Xunit;
namespace DataPitcher.UnitTests.Transfer;
public sealed class TransferBatcherTests
{
    private const int MiB = 1024 * 1024;
    [Fact] public async Task ReadBatchesAsync_WhenTinyRowsReachMaximumRows_ClosesOnRowLimit()
    {
        var batches = await BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(3, 8 * MiB), 1)), [R(1, 1), R(2, 1), R(3, 1), R(4, 1)]);
        Assert.Equal([3, 1], batches.Select(batch => batch.Rows.Count)); Assert.Equal([1L, 2L], batches.Select(batch => batch.Sequence));
    }
    [Fact] public async Task ReadBatchesAsync_WhenWideRowsReachTargetPayload_ClosesOnByteLimit()
    {
        var batches = await BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(10, 8 * MiB), 1)), [R(1, 4 * MiB), R(2, 4 * MiB), R(3, 5 * MiB), R(4, 5 * MiB)]);
        Assert.Equal([2, 1, 1], batches.Select(batch => batch.Rows.Count)); Assert.Equal(8L * MiB, batches[0].PayloadBytes);
    }
    [Fact] public async Task ReadBatchesAsync_WhenOneRowExceedsTarget_EmitsThatRowAlone()
    {
        var batches = await BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(10, 8 * MiB), 1)), [R(1, 9 * MiB), R(2, 1)]);
        Assert.Equal([1, 1], batches.Select(batch => batch.Rows.Count)); Assert.Equal(9L * MiB, batches[0].PayloadBytes);
    }
    [Fact] public async Task ReadBatchesAsync_WhenConverterIsSupplied_WritesConvertedRows()
    {
        var batches = await BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1)), [R(1, 1)], new IncrementingConverter());
        Assert.Equal(2, Assert.Single(batches).Rows[0].Values[0]);
    }
    [Fact] public async Task ReadBatchesAsync_WhenSourceIsEmpty_EmitsNoBatch()
    {
        var batches = await BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1)), []);
        Assert.Empty(batches);
    }
    [Property(MaxTest = 100)] public void ReadBatchesAsync_PartitionsEveryGeneratedInputExactlyOnce(byte[] payloads)
    {
        var rows = payloads.Select((payload, index) => R(index, payload + 1L)).ToArray();
        var batches = BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(3, 8 * MiB), 1)), rows).GetAwaiter().GetResult();
        var flattened = batches.SelectMany(batch => batch.Rows).Select(row => (int)row.Values[0]!).ToArray();
        Assert.Equal(Enumerable.Range(0, rows.Length), flattened); Assert.Equal(flattened.Length, flattened.Distinct().Count());
    }
    private static TransferRow R(int value, long bytes) => new TransferRow([value], bytes);
    private static async Task<List<TransferBatch>> BatchesAsync(TransferBatcher batcher, IEnumerable<TransferRow> rows, ITransferRowConverter? converter = null)
    { var result = new List<TransferBatch>(); await foreach (var batch in batcher.ReadBatchesAsync(new Source(rows), converter, CancellationToken.None)) result.Add(batch); return result; }
    private sealed class Source : ITransferRowSource
    {
        private readonly IReadOnlyList<TransferRow> _rows; public Source(IEnumerable<TransferRow> rows) => _rows = rows.ToArray();
        public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { foreach (var row in _rows) { cancellationToken.ThrowIfCancellationRequested(); yield return row; await Task.CompletedTask; } }
    }
    private sealed class IncrementingConverter : ITransferRowConverter
    { public ValueTask<TransferRow> ConvertAsync(TransferRow row, CancellationToken cancellationToken) => ValueTask.FromResult(new TransferRow([(int)row.Values[0]! + 1], row.PayloadBytes)); }
}
