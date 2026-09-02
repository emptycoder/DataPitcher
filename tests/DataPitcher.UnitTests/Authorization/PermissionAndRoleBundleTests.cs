using DataPitcher.Core.Authorization;
using Xunit;

namespace DataPitcher.UnitTests.Authorization;

public sealed class PermissionAndRoleBundleTests
{
    [Fact]
    public void Permissions_ExposeTheCompleteClosedVocabulary() =>
        Assert.Equal(["Audit.Read", "Audit.Write", "AuthProviders.Manage", "Connections.Read", "Connections.Write", "Plans.Read", "Plans.Seal", "Plans.Write", "RoleMappings.Manage", "Schema.Read", "Schema.Write", "Selections.RawSql", "Selections.Read", "Selections.Write", "Transfers.ConstraintOverride", "Transfers.Read", "Transfers.Start", "Transfers.TriggerOverride", "Transfers.UsePotentiallyLossyMapping", "Transfers.Write"], Permissions.All.Select(permission => permission.Value));

    [Fact]
    public void RoleBundles_ViewerHasEveryReadPermissionOnly() =>
        Assert.Equal(new PermissionSet([Permissions.AuditRead, Permissions.ConnectionsRead, Permissions.PlansRead, Permissions.SchemaRead, Permissions.SelectionsRead, Permissions.TransfersRead]), RoleBundles.For(Role.Viewer));

    [Fact]
    public void RoleBundles_PlannerAddsPlanningWritesAndNoTransferStart() =>
        Assert.Equal(new PermissionSet([Permissions.AuditRead, Permissions.ConnectionsRead, Permissions.ConnectionsWrite, Permissions.PlansRead, Permissions.PlansSeal, Permissions.PlansWrite, Permissions.SchemaRead, Permissions.SchemaWrite, Permissions.SelectionsRawSql, Permissions.SelectionsRead, Permissions.SelectionsWrite, Permissions.TransfersRead]), RoleBundles.For(Role.Planner));

    [Fact]
    public void RoleBundles_OperatorAddsTransferExecutionAndOverrides() =>
        Assert.Equal(new PermissionSet([Permissions.AuditRead, Permissions.ConnectionsRead, Permissions.PlansRead, Permissions.SchemaRead, Permissions.SelectionsRead, Permissions.TransfersConstraintOverride, Permissions.TransfersRead, Permissions.TransfersStart, Permissions.TransfersTriggerOverride, Permissions.TransfersUsePotentiallyLossyMapping]), RoleBundles.For(Role.Operator));

    [Fact]
    public void RoleBundles_AdministratorHasEveryPermission() => Assert.Equal(new PermissionSet(Permissions.All), RoleBundles.For(Role.Administrator));

    [Fact]
    public void PermissionSet_UnionAndRemovalRemainImmutableAndMonotonic()
    {
        var full = new PermissionSet([Permissions.PlansSeal, Permissions.TransfersStart]);
        var reduced = full.Without(Permissions.PlansSeal);
        Assert.True(reduced.IsSubsetOf(full));
        Assert.True(full.Contains(Permissions.PlansSeal));
        Assert.Equal(full, reduced.Union(new PermissionSet([Permissions.PlansSeal])));
        Assert.Equal(full.GetHashCode(), new PermissionSet([Permissions.PlansSeal, Permissions.TransfersStart]).GetHashCode());
        Assert.True(full.Equals((object)new PermissionSet([Permissions.PlansSeal, Permissions.TransfersStart])));
        Assert.False(full.Equals(null));
    }

    [Fact]
    public void PermissionSet_WhenComparedWithAnUnrelatedObject_IsNotEqual() =>
        Assert.False(new PermissionSet([Permissions.PlansSeal]).Equals((object)"not a permission set"));

    [Fact]
    public void RoleBundles_WhenRoleIsUnknown_RejectsIt() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RoleBundles.For((Role)99));
}
