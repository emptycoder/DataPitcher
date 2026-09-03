using DataPitcher.Application.Plans;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Application.Worker;

public sealed class PlanJobRunCatalog(IPlanRepository plans) : IJobRunCatalog
{
    public async Task<TransferRun> LoadAsync(TransferJob job, CancellationToken cancellationToken)
    {
        var plan = (await plans.FindAsync(job.PlanId, cancellationToken))!;
        var content = (await plans.LoadContentAsync(job.PlanId, cancellationToken))!;
        if (!content.IsSealedByCurrentVersion)
            throw new StalePlanException(content.SealingVersion);
        return new(
            job.JobId,
            job.RunId,
            plan.CanonicalHash!,
            content.TransferMode is TransferMode.ResumableStaged,
            content.Source.ConnectionId,
            content.Target.ConnectionId,
            content.TransferMode,
            job.PlanId
        );
    }
}
