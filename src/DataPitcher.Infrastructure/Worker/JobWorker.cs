using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Time;
using DataPitcher.Infrastructure.Events;
using Microsoft.Extensions.Hosting;

namespace DataPitcher.Infrastructure.Worker;

public sealed class JobWorker(
    IJobControl jobs, IJobRunCatalog catalog, ITargetRunSessionFactory targets,
    ITransferReadSessionFactory sources, RecoveryCoordinator recovery, LeaseRenewer renewer,
    IControlCheckpointMirror mirror, IJobEventWriter events, IWorkerFaults faults, IWorkerDelay delay, IClock clock,
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
        await events.AppendAsync(new JobEventAppend(claim.Job.JobId, "state", new JobEventPayload("preparing", 0, 0)), leaseLost.Token);
        var run = await catalog.LoadAsync(claim.Job, leaseLost.Token);
        await using var target = await targets.OpenAsync(run, leaseLost.Token);
        var checkpoint = await recovery.RecoverAsync(claim, run, target, leaseLost.Token);
        await jobs.MarkRunningAsync(claim.Lease, leaseLost.Token);
        await events.AppendAsync(new JobEventAppend(claim.Job.JobId, "state", new JobEventPayload("running", checkpoint.RowCount, checkpoint.BytesTransferred)), leaseLost.Token);
        await using var source = await sources.OpenKeysetAsync(run, checkpoint.LastStableKey, leaseLost.Token);
        for (TransferUnit? unit; (unit = await source.ReadNextAsync(leaseLost.Token)) is not null;)
        {
            await faults.HitAsync(TransferFaultPoint.BeforeTargetCommit, leaseLost.Token);
            checkpoint = await target.ApplyAsync(run, claim.Lease, unit, leaseLost.Token);
            await events.AppendAsync(new JobEventAppend(claim.Job.JobId, "progress", new JobEventPayload("running", checkpoint.RowCount, checkpoint.BytesTransferred)), leaseLost.Token);
            await faults.HitAsync(TransferFaultPoint.AfterTargetCommitBeforeControlMirror, leaseLost.Token);
            await mirror.OverwriteAsync(checkpoint, leaseLost.Token);
            var state = await jobs.GetStateAsync(claim.Job.JobId, leaseLost.Token);
            if (state is JobState.Cancelling)
            {
                using var cleanup = new CancellationTokenSource();
                await source.DiscardUncommittedAsync(cleanup.Token);
                await target.DiscardUncommittedAsync(cleanup.Token);
                await jobs.MarkCancelledAsync(claim.Lease, cleanup.Token);
                return;
            }
            if (state is JobState.Pausing)
            {
                await source.DiscardUncommittedAsync(leaseLost.Token);
                await jobs.MarkPausedAsync(claim.Lease, leaseLost.Token);
                return;
            }
        }
        await jobs.MarkVerifyingAsync(claim.Lease, leaseLost.Token);
        await events.AppendAsync(new JobEventAppend(claim.Job.JobId, "state", new JobEventPayload("verifying", checkpoint.RowCount, checkpoint.BytesTransferred)), leaseLost.Token);
    }
    finally { renewalStop.Cancel(); await renewal; }
}
}
