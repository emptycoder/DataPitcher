using DataPitcher.Application.Plans;
using DataPitcher.ControlStore;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class PlanStoreTests
{
    [Fact]
    public async Task SaveAsync_WhenCreatedWithAssociations_PersistsThem()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new PlanStore(fixture.Database, fixture.Clock);
        var planId = Guid.NewGuid();
        var selectionId = Guid.NewGuid();
        var sourceConnectionId = Guid.NewGuid();
        var targetConnectionId = Guid.NewGuid();

        _ = await store.SaveAsync(
            planId,
            "Plan",
            null,
            "\"0\"",
            CancellationToken.None,
            selectionId,
            sourceConnectionId,
            targetConnectionId
        );
        var plan = await store.FindAsync(planId, CancellationToken.None);

        Assert.Equal(selectionId, plan!.SelectionId);
        Assert.Equal(sourceConnectionId, plan.SourceConnectionId);
        Assert.Equal(targetConnectionId, plan.TargetConnectionId);
    }

    [Fact]
    public async Task RecordSealFailureAsync_KeepsTheReasonUntilThePlanIsSealedAgain()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new PlanStore(fixture.Database, fixture.Clock);
        var planId = Guid.NewGuid();
        _ = await store.SaveAsync(planId, "Plan", null, "\"0\"", CancellationToken.None);

        await store.RecordSealFailureAsync(
            planId,
            "unorderable_cycle",
            "No write order satisfies the keys.",
            CancellationToken.None
        );
        var failed = (await store.FindAsync(planId, CancellationToken.None))!;
        await store.SealAsync(planId, Plans.PlanTestData.Baseline(), CancellationToken.None);
        var resealed = (await store.FindAsync(planId, CancellationToken.None))!;

        Assert.Equal("unorderable_cycle", failed.SealFailureCode);
        Assert.Equal("No write order satisfies the keys.", failed.SealFailureDetail);
        Assert.Null(resealed.SealFailureCode);
        Assert.Null(resealed.SealFailureDetail);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordSealFailureAsync(Guid.NewGuid(), "seal_failed", "missing", CancellationToken.None)
        );
    }

    [Fact]
    public async Task SaveAsync_WhenUpdatedWithAssociations_PersistsThem()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new PlanStore(fixture.Database, fixture.Clock);
        var planId = Guid.NewGuid();
        var selectionId = Guid.NewGuid();
        var sourceConnectionId = Guid.NewGuid();
        var targetConnectionId = Guid.NewGuid();
        _ = await store.SaveAsync(planId, "Plan", null, "\"0\"", CancellationToken.None);

        _ = await store.SaveAsync(
            planId,
            "Plan",
            null,
            "\"1\"",
            CancellationToken.None,
            selectionId,
            sourceConnectionId,
            targetConnectionId
        );
        var plan = await store.FindAsync(planId, CancellationToken.None);

        Assert.Equal(selectionId, plan!.SelectionId);
        Assert.Equal(sourceConnectionId, plan.SourceConnectionId);
        Assert.Equal(targetConnectionId, plan.TargetConnectionId);
    }
}
