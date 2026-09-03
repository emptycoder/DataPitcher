using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Application.Worker;

public interface IWorkerDelay
{
    Task UntilAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken);
}

public sealed class ClockWorkerDelay(IClock clock) : IWorkerDelay
{
    public Task UntilAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Delay(dueUtc <= clock.UtcNow ? TimeSpan.Zero : dueUtc - clock.UtcNow, cancellationToken);
    }
}
