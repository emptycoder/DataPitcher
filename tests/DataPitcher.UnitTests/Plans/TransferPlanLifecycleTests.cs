using DataPitcher.Core.Plans;
using Xunit;

namespace DataPitcher.UnitTests.Plans;

public sealed class TransferPlanLifecycleTests
{
    [Fact]
    public void Plan_WhenConnectionChanges_InvalidatesSeal() => AssertInvalidates("connection");

    [Fact]
    public void Plan_WhenDatabaseIdentityChanges_InvalidatesSeal() => AssertInvalidates("database identity");

    [Fact]
    public void Plan_WhenSchemaSnapshotChanges_InvalidatesSeal() => AssertInvalidates("schema snapshot");

    [Fact]
    public void Plan_WhenTargetSchemaSnapshotChanges_InvalidatesSeal() => AssertInvalidates("target schema snapshot");

    [Fact]
    public void Plan_WhenSelectionChanges_InvalidatesSeal() => AssertInvalidates("selection");

    [Fact]
    public void Plan_WhenSelectionParameterChanges_InvalidatesSeal() => AssertInvalidates("selection parameter");

    [Fact]
    public void Plan_WhenStableKeyDefinitionChanges_InvalidatesSeal() => AssertInvalidates("stable key");

    [Fact]
    public void Plan_WhenRelationshipPolicyChanges_InvalidatesSeal() => AssertInvalidates("relationship policy");

    [Fact]
    public void Plan_WhenRelationshipColumnOrderChanges_InvalidatesSeal() =>
        AssertInvalidates("relationship column order");

    [Fact]
    public void Plan_WhenConflictPolicyChanges_InvalidatesSeal() => AssertInvalidates("conflict policy");

    [Fact]
    public void Plan_WhenColumnMappingChanges_InvalidatesSeal() => AssertInvalidates("column mapping");

    [Fact]
    public void Plan_WhenTransferModeChanges_InvalidatesSeal() => AssertInvalidates("transfer mode");

    [Fact]
    public void Plan_WhenConsistencyModeChanges_InvalidatesSeal() => AssertInvalidates("consistency mode");

    [Fact]
    public void Plan_WhenTriggerStrategyChanges_InvalidatesSeal() => AssertInvalidates("trigger strategy");

    [Fact]
    public void Plan_WhenConstraintStrategyChanges_InvalidatesSeal() => AssertInvalidates("constraint strategy");

    [Fact]
    public void Plan_WhenOnlyDisplayNameChanges_DoesNotInvalidateSeal()
    {
        var lifecycle = new TransferPlanLifecycle(
            new(
                "First label",
                "note",
                "operator-a",
                new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero),
                PlanTestData.Baseline()
            )
        );
        var sealedPlan = lifecycle.Seal(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            new DateTimeOffset(2026, 9, 2, 11, 0, 0, TimeSpan.Zero)
        );
        lifecycle.Replace(
            new(
                "Second label",
                "note",
                "operator-a",
                new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero),
                PlanTestData.Baseline()
            )
        );
        Assert.Same(sealedPlan, lifecycle.CurrentSeal);
        Assert.Equal(1, lifecycle.CurrentSeal!.Identity.Version);
    }

    [Fact]
    public void SealedPlan_WhenCollectionsAreDowncast_RejectsIndexerAssignment()
    {
        var lifecycle = new TransferPlanLifecycle(
            new("Label", null, "operator-a", DateTimeOffset.UnixEpoch, PlanTestData.Baseline())
        );
        var sealedPlan = lifecycle.Seal(Guid.Empty, DateTimeOffset.UnixEpoch);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PlanTable>)sealedPlan.Content.Tables)[0] = sealedPlan.Content.Tables[0]
        );
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ColumnMapping>)sealedPlan.Content.Tables[0].Mapping.Columns)[0] = new("Id", "Changed")
        );
    }

    private static void AssertInvalidates(string material)
    {
        var lifecycle = new TransferPlanLifecycle(
            new("Label", null, "operator-a", DateTimeOffset.UnixEpoch, PlanTestData.Baseline())
        );
        var before = lifecycle.Seal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), DateTimeOffset.UnixEpoch);
        lifecycle.Replace(new("Label", null, "operator-a", DateTimeOffset.UnixEpoch, PlanTestData.Changed(material)));
        Assert.Null(lifecycle.CurrentSeal);
        var after = lifecycle.Seal(before.Identity.PlanId, DateTimeOffset.UnixEpoch.AddMinutes(1));
        Assert.Equal(before.Identity.Version + 1, after.Identity.Version);
        Assert.NotEqual(before.CanonicalHash, after.CanonicalHash);
    }
}
