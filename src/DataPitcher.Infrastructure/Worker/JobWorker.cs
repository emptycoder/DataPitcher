using DataPitcher.Infrastructure.Time;
using Microsoft.Extensions.Hosting;

namespace DataPitcher.Infrastructure.Worker;

public sealed class JobWorker(
    IJobControl jobs, IJobRunCatalog catalog, ITargetRunSessionFactory targets,
    ITransferReadSessionFactory sources, LeaseRenewer renewer,
    IControlCheckpointMirror mirror, IWorkerFaults faults, IWorkerDelay delay, IClock clock,
    string ownerId, TimeSpan leaseTtl, TimeSpan pollInterval) : BackgroundService
{
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        var claim = await jobs.TryClaimNextAsync(ownerId, leaseTtl, stoppingToken);
        if (claim is null) { await delay.UntilAsync(clock.UtcNow.Add(pollInterval), stoppingToken); continue; }
        await RunClaimAsync(claim, stoppingToken);
    }
}

private async Task RunClaimAsync(JobClaim claim, CancellationToken stoppingToken)
{
    using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    using var renewalStop = new CancellationTokenSource();
    var renewal = renewer.RunAsync(claim.Lease, leaseTtl, leaseLost, renewalStop.Token);
    try
    {
        await jobs.PrepareAsync(claim, leaseLost.Token);
        var run = await catalog.LoadAsync(claim.Job, leaseLost.Token);
        await using var target = await targets.OpenAsync(run, leaseLost.Token);
        await jobs.MarkRunningAsync(claim.Lease, leaseLost.Token);
        await using var source = await sources.OpenKeysetAsync(run, null, leaseLost.Token);
        for (TransferUnit? unit; (unit = await source.ReadNextAsync(leaseLost.Token)) is not null;)
        {
            await faults.HitAsync(TransferFaultPoint.BeforeTargetCommit, leaseLost.Token);
            var checkpoint = await target.ApplyAsync(run, claim.Lease, unit, leaseLost.Token);
            await faults.HitAsync(TransferFaultPoint.AfterTargetCommitBeforeControlMirror, leaseLost.Token);
            await mirror.OverwriteAsync(checkpoint, leaseLost.Token);
        }
        await jobs.MarkVerifyingAsync(claim.Lease, leaseLost.Token);
    }
    finally { renewalStop.Cancel(); await renewal; }
}
}
