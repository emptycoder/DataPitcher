using DataPitcher.Core.Identity;
using DataPitcher.Infrastructure.Worker;
using Xunit;

namespace DataPitcher.UnitTests.Worker;

public sealed class WorkerContractsTests
{
    [Fact]
    public async Task Faults_WhenPointIsConfigured_ThrowsOnlyForThatPoint()
    {
        var faults = new ScriptedWorkerFaults(TransferFaultPoint.BeforeTargetCommit);
        await Assert.ThrowsAsync<SimulatedWorkerFaultException>(() => faults.HitAsync(TransferFaultPoint.BeforeTargetCommit, CancellationToken.None));
        await faults.HitAsync(TransferFaultPoint.BeforeTargetCommit, CancellationToken.None);
        Assert.Contains(TransferFaultPoint.PermanentFailure, Enum.GetValues<TransferFaultPoint>());
    }

    [Fact]
    public void TransferUnit_WhenAtomicComponent_IsPausableOnlyAfterItsCommit()
    {
        var unit = new TransferUnit(4, new StableKey([new KeyComponent("Id", 9)]), 3, TransferUnitKind.AtomicComponent);
        Assert.True(unit.CanPauseAfterCommit);
        Assert.Equal(4, unit.BatchSequence);
        Assert.Equal(3, unit.RowCount);
    }

    private sealed class ScriptedWorkerFaults(TransferFaultPoint point) : IWorkerFaults
    {
        private bool _pending = true;
        public Task HitAsync(TransferFaultPoint current, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pending && current == point) { _pending = false; throw new SimulatedWorkerFaultException(point); }
            return Task.CompletedTask;
        }
    }
}
