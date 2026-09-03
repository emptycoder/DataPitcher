using DataPitcher.Application.Connections;
using DataPitcher.Application.Events;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Extensions.Hosting;

namespace DataPitcher.Application.Worker;

public sealed class JobWorker(
    IJobControl jobs,
    IJobRunCatalog catalog,
    ITransferConnectionRevalidator revalidator,
    ITargetRunSessionFactory targets,
    ITransferReadSessionFactory sources,
    RecoveryCoordinator recovery,
    LeaseRenewer renewer,
    IControlCheckpointMirror mirror,
    IJobEventWriter events,
    IWorkerFaults faults,
    IWorkerDelay delay,
    IClock clock,
    string ownerId,
    TimeSpan leaseTtl,
    TimeSpan pollInterval
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claim = await jobs.TryClaimNextAsync(ownerId, leaseTtl, stoppingToken);
                if (claim is null)
                {
                    await delay.UntilAsync(clock.UtcNow.Add(pollInterval), stoppingToken);
                    continue;
                }
                await RunClaimAsync(claim, stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task RunClaimAsync(JobClaim claim, CancellationToken stoppingToken)
    {
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var renewalStop = new CancellationTokenSource();
        var renewal = renewer.RunAsync(claim.Lease, leaseTtl, leaseLost, renewalStop.Token);
        long rows = 0;
        long bytes = 0;
        long skipped = 0;
        try
        {
            await jobs.PrepareAsync(claim, leaseLost.Token);
            await events.AppendAsync(
                new JobEventAppend(claim.Job.JobId, "state", new JobEventPayload("preparing", 0, 0)),
                leaseLost.Token
            );
            var run = await catalog.LoadAsync(claim.Job, leaseLost.Token);
            await revalidator.RevalidateAsync(run, leaseLost.Token);
            await using var target = await targets.OpenAsync(run, leaseLost.Token);
            var checkpoint = await recovery.RecoverAsync(claim, run, target, leaseLost.Token);
            (rows, bytes) = (checkpoint.RowCount, checkpoint.BytesTransferred);
            await jobs.MarkRunningAsync(claim.Lease, leaseLost.Token);
            await events.AppendAsync(
                new JobEventAppend(
                    claim.Job.JobId,
                    "state",
                    new JobEventPayload("running", checkpoint.RowCount, checkpoint.BytesTransferred)
                ),
                leaseLost.Token
            );
            await using var source = await sources.OpenKeysetAsync(
                run,
                checkpoint.LastStableKey,
                leaseLost.Token,
                checkpoint.LastTable
            );
            for (TransferUnit? unit; (unit = await source.ReadNextAsync(leaseLost.Token)) is not null; )
            {
                await faults.HitAsync(TransferFaultPoint.BeforeTargetCommit, leaseLost.Token);
                checkpoint = await target.ApplyAsync(run, claim.Lease, unit, leaseLost.Token);
                (rows, bytes) = (checkpoint.RowCount, checkpoint.BytesTransferred);
                if (checkpoint.SkippedRows > skipped)
                {
                    var table = unit.Table is null ? "the target" : unit.Table.Schema + "." + unit.Table.Name;
                    await AnnounceAsync(
                        claim.Job.JobId,
                        "conflict",
                        "running",
                        rows,
                        bytes,
                        $"{checkpoint.SkippedRows - skipped} row(s) in {table} already existed in the target and were skipped.",
                        leaseLost.Token
                    );
                    skipped = checkpoint.SkippedRows;
                }
                await events.AppendAsync(
                    new JobEventAppend(
                        claim.Job.JobId,
                        "progress",
                        new JobEventPayload("running", checkpoint.RowCount, checkpoint.BytesTransferred)
                    ),
                    leaseLost.Token
                );
                await faults.HitAsync(TransferFaultPoint.AfterTargetCommitBeforeControlMirror, leaseLost.Token);
                await mirror.OverwriteAsync(checkpoint, leaseLost.Token);
                var state = await jobs.GetStateAsync(claim.Job.JobId, leaseLost.Token);
                if (state is JobState.Cancelling)
                {
                    using var cleanup = new CancellationTokenSource();
                    await source.DiscardUncommittedAsync(cleanup.Token);
                    await target.DiscardUncommittedAsync(cleanup.Token);
                    await jobs.MarkCancelledAsync(claim.Lease, cleanup.Token);
                    await AnnounceAsync(claim.Job.JobId, "cancelled", rows, bytes, null, cleanup.Token);
                    return;
                }
                if (state is JobState.Pausing)
                {
                    await source.DiscardUncommittedAsync(leaseLost.Token);
                    await jobs.MarkPausedAsync(claim.Lease, leaseLost.Token);
                    await AnnounceAsync(claim.Job.JobId, "paused", rows, bytes, null, leaseLost.Token);
                    return;
                }
            }
            await jobs.MarkVerifyingAsync(claim.Lease, leaseLost.Token);
            await events.AppendAsync(
                new JobEventAppend(
                    claim.Job.JobId,
                    "state",
                    new JobEventPayload("verifying", checkpoint.RowCount, checkpoint.BytesTransferred)
                ),
                leaseLost.Token
            );
            await target.CompleteAsync(run, leaseLost.Token);
            await jobs.MarkSucceededAsync(claim.Lease, leaseLost.Token);
            await AnnounceAsync(claim.Job.JobId, "succeeded", rows, bytes, null, leaseLost.Token);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            var (code, detail) = Describe(exception);
            await jobs.MarkFailedAsync(claim.Lease, code, detail, stoppingToken);
            await AnnounceAsync(claim.Job.JobId, "failed", rows, bytes, detail, stoppingToken);
        }
        finally
        {
            renewalStop.Cancel();
            await renewal;
        }
    }

    /// <summary>Publishes a state change so live pages learn about it without polling; never fails the job.</summary>
    private Task AnnounceAsync(
        Guid jobId,
        string state,
        long rows,
        long bytes,
        string? detail,
        CancellationToken cancellationToken
    ) => AnnounceAsync(jobId, "state", state, rows, bytes, detail, cancellationToken);

    private async Task AnnounceAsync(
        Guid jobId,
        string eventType,
        string state,
        long rows,
        long bytes,
        string? detail,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await events.AppendAsync(
                new JobEventAppend(jobId, eventType, new JobEventPayload(state, rows, bytes, detail)),
                cancellationToken
            );
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The job record already carries the state; the page falls back to polling it.
        }
    }

    /// <summary>A fixed failure code plus the reason an operator can act on. Driver messages carry no secrets.</summary>
    internal static (string Code, string Detail) Describe(Exception exception)
    {
        var root = exception.GetBaseException();
        var message = root.Message.Length > 2000 ? root.Message[..2000] : root.Message;
        var code = exception switch
        {
            ConnectionNotHealthyException => "connection_unhealthy",
            TargetVerificationException => "verification_failed",
            NotSupportedException => "not_supported",
            _ => "transfer_failed",
        };
        return (code, message);
    }
}
