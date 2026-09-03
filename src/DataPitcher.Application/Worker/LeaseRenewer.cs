using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Application.Worker;

public sealed class LeaseRenewer(ILeaseStore leases, IWorkerDelay delay)
{
    public async Task RunAsync(
        LeaseGrant lease,
        TimeSpan ttl,
        CancellationTokenSource leaseLost,
        CancellationToken stopToken
    )
    {
        var current = lease;
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                await delay.UntilAsync(current.RenewAfterUtc, stopToken);
                var renewed = leases.RenewIfDue(current, ttl);
                if (renewed is null)
                {
                    leaseLost.Cancel();
                    return;
                }
                current = renewed;
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { }
    }
}
