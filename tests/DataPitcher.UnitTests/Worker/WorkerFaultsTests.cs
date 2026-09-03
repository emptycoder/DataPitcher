using DataPitcher.Infrastructure.Worker;
using Xunit;

namespace DataPitcher.UnitTests.Worker;

public sealed class WorkerFaultsTests
{
    [Fact]
    public async Task HitAsync_ForAnyFaultPoint_NeverThrows()
    {
        var faults = new NoOpWorkerFaults();

        foreach (var point in Enum.GetValues<TransferFaultPoint>())
            await faults.HitAsync(point, CancellationToken.None);
    }

    [Fact]
    public async Task HitAsync_WhenCancellationIsRequested_ThrowsOperationCanceledException()
    {
        var faults = new NoOpWorkerFaults();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => faults.HitAsync(TransferFaultPoint.BeforeTargetCommit, cancellation.Token));
    }
}
