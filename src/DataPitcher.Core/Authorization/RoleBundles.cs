namespace DataPitcher.Core.Authorization;

public static class RoleBundles
{
    private static readonly PermissionSet Viewer = new([Permissions.AuditRead, Permissions.ConnectionsRead, Permissions.PlansRead, Permissions.SchemaRead, Permissions.SelectionsRead, Permissions.TransfersRead]);
    private static readonly PermissionSet Planner = Viewer.Union(new([Permissions.ConnectionsWrite, Permissions.PlansSeal, Permissions.PlansWrite, Permissions.SchemaWrite, Permissions.SelectionsRawSql, Permissions.SelectionsWrite]));
    private static readonly PermissionSet Operator = Viewer.Union(new([Permissions.TransfersConstraintOverride, Permissions.TransfersStart, Permissions.TransfersTriggerOverride, Permissions.TransfersUsePotentiallyLossyMapping]));
    private static readonly PermissionSet Administrator = new(Permissions.All);

    public static PermissionSet For(Role role) => role switch
    {
        Role.Viewer => Viewer,
        Role.Planner => Planner,
        Role.Operator => Operator,
        Role.Administrator => Administrator,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}
