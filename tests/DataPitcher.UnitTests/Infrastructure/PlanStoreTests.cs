using DataPitcher.Infrastructure.Plans;
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
