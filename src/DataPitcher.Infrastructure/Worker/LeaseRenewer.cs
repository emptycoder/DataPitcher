using DataPitcher.Infrastructure.Leasing;

namespace DataPitcher.Infrastructure.Worker;

public sealed class LeaseRenewer(LeaseStore leases, IWorkerDelay delay)
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
