using DataPitcher.Core.Plans;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Plans;

namespace DataPitcher.Infrastructure.Worker;

public sealed class PlanJobRunCatalog(PlanStore plans) : IJobRunCatalog
{
    public async Task<TransferRun> LoadAsync(TransferJob job, CancellationToken cancellationToken)
    {
        var plan = (await plans.FindAsync(job.PlanId, cancellationToken))!;
        var content = (await plans.LoadContentAsync(job.PlanId, cancellationToken))!;
        return new(job.JobId, job.RunId, plan.CanonicalHash!, content.TransferMode is TransferMode.ResumableStaged, content.Source.ConnectionId, content.Target.ConnectionId, content.TransferMode, job.PlanId);
    }
}
