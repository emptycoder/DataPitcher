namespace DataPitcher.Infrastructure.Worker;

public sealed class NoOpWorkerFaults : IWorkerFaults
{
    public Task HitAsync(TransferFaultPoint point, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
