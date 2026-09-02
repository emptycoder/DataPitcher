using DataPitcher.Infrastructure.Time;

namespace DataPitcher.Infrastructure.Worker;

public interface IWorkerDelay { Task UntilAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken); }
public sealed class ClockWorkerDelay(IClock clock) : IWorkerDelay
{
    public Task UntilAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Delay(dueUtc <= clock.UtcNow ? TimeSpan.Zero : dueUtc - clock.UtcNow, cancellationToken);
    }
}
