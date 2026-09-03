using DataPitcher.Application.Plans;
using DataPitcher.Application.Worker;
using DataPitcher.ControlStore;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using DataPitcher.UnitTests.Infrastructure;
using DataPitcher.UnitTests.Plans;
using LinqToDB.Data;
using Xunit;

namespace DataPitcher.UnitTests.Worker;

public sealed class PlanJobRunCatalogTests
{
    [Theory]
    [InlineData(TransferMode.ResumableStaged, true)]
    [InlineData(TransferMode.DirectFast, false)]
    public async Task PlanJobRunCatalog_WhenPlanIsSealed_UsesTheStoredSealAndPlanRunDetails(
        TransferMode transferMode,
        bool supportsDurableResume
    )
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var plans = new PlanStore(fixture.Database, fixture.Clock);
        var planId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var content = PlanTestData.Baseline(
            new("PostgreSql", "source", "source-fingerprint", sourceId),
            new("PostgreSql", "target", "target-fingerprint", targetId),
            transfer: transferMode
        );
        await plans.SaveAsync(planId, "plan", null, "", CancellationToken.None);
        await plans.SealAsync(planId, content, CancellationToken.None);
        using (var db = fixture.Database.Open())
            db.Execute(
                "UPDATE Plans SET CanonicalHash = @hash WHERE PlanId = @planId",
                new DataParameter("hash", "stored-seal"),
                new DataParameter("planId", planId.ToString())
            );
        var job = new TransferJob(Guid.NewGuid(), Guid.NewGuid(), planId, "job", DataPitcher.Core.Jobs.JobState.Queued);
        IJobRunCatalog catalog = new PlanJobRunCatalog(plans);

        var run = await catalog.LoadAsync(job, CancellationToken.None);

        Assert.Equal(job.JobId, run.JobId);
        Assert.Equal(job.RunId, run.RunId);
        Assert.Equal(planId, run.PlanId);
        Assert.Equal("stored-seal", run.ManifestSealHash);
        Assert.Equal(supportsDurableResume, run.SupportsDurableResume);
        Assert.Equal(sourceId, run.SourceConnectionId);
        Assert.Equal(targetId, run.TargetConnectionId);
        Assert.Equal(transferMode, run.TransferMode);
    }
}
