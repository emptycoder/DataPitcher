using System.Runtime.CompilerServices;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using Xunit;
namespace DataPitcher.UnitTests.Transfer;
public sealed class TransferContractsTests
{
    private const int MiB = 1024 * 1024;
    [Fact] public void TransferRow_WhenSourceValuesChange_RetainsReadOnlyCopy()
    {
        object?[] values = [1, "before"]; var row = new TransferRow(values, 12); values[1] = "after";
        Assert.Equal("before", row.Values[1]); Assert.Equal(12, row.PayloadBytes);
        Assert.Throws<NotSupportedException>(() => ((IList<object?>)row.Values)[0] = 2);
    }
    [Theory]
    [InlineData(0, 8 * MiB)] [InlineData(1, 0)] [InlineData(1, 33 * MiB)] [InlineData(1, 8 * MiB, 3)]
    public void TransferPipelineOptions_WhenLimitsAreOutsideSupportedBounds_RejectsThem(int rows, int bytes, int queued = 1)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferPipelineOptions(new BatchTarget(rows, bytes), queued));
    }
    [Fact] public void TransferPipelineOptions_WhenLimitsAreValid_ExposesTheSealedTargetAndQueueBound()
    {
        var target = new BatchTarget(2, 8 * MiB); var options = new TransferPipelineOptions(target, 2);
        Assert.Equal(target, options.BatchTarget); Assert.Equal(2, options.MaximumQueuedBatches);
    }
    [Fact] public void TransferBatch_WhenConstructed_ExposesSequenceRowsAndPayload()
    {
        var batch = new TransferBatch(1, [new TransferRow([1], 4), new TransferRow([2], 6)]);
        Assert.Equal(1, batch.Sequence); Assert.Equal(2, batch.Rows.Count); Assert.Equal(10, batch.PayloadBytes);
    }
    [Fact] public void TransferValues_WhenBatchIsEmptyOrSequenceIsInvalid_RejectConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferBatch(0, [new TransferRow([1], 1)]));
        Assert.Throws<ArgumentException>(() => new TransferBatch(1, Array.Empty<TransferRow>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferRow([1], -1));
    }
    [Fact] public async Task TransferContracts_CanReadConvertAndWriteOneRow()
    {
        ITransferRowSource source = new SingleRowSource(new TransferRow([1], 4)); ITransferRowConverter converter = new IncrementingConverter(); ITransferBatchWriter writer = new CountingWriter();
        await foreach (var row in source.ReadAsync(CancellationToken.None))
        {
            var converted = await converter.ConvertAsync(row, CancellationToken.None);
            var result = await writer.WriteAsync(new TransferBatch(1, [converted]), CancellationToken.None);
            Assert.Equal(2, converted.Values[0]); Assert.Equal(1, result.Inserted); Assert.Equal(0, result.Updated); Assert.Equal(0, result.Skipped); Assert.Equal(0, result.Failed); Assert.Equal(4, result.BytesWritten);
        }
    }
    private sealed class SingleRowSource(TransferRow row) : ITransferRowSource
    {
        public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); yield return row; await Task.CompletedTask; }
    }
    private sealed class IncrementingConverter : ITransferRowConverter
    { public ValueTask<TransferRow> ConvertAsync(TransferRow row, CancellationToken cancellationToken) => ValueTask.FromResult(new TransferRow([(int)row.Values[0]! + 1], row.PayloadBytes)); }
    private sealed class CountingWriter : ITransferBatchWriter
    { public Task<BatchWriteResult> WriteAsync(TransferBatch batch, CancellationToken cancellationToken) => Task.FromResult(new BatchWriteResult(batch.Rows.Count, 0, 0, 0, batch.PayloadBytes)); }
}
