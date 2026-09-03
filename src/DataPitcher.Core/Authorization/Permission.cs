namespace DataPitcher.Core.Authorization;

public sealed record Permission
{
    internal Permission(string value) => Value = value;

    public string Value { get; }
}

public static class Permissions
{
    public static Permission ConnectionsRead { get; } = new("Connections.Read");
    public static Permission ConnectionsWrite { get; } = new("Connections.Write");
    public static Permission SchemaRead { get; } = new("Schema.Read");
    public static Permission SchemaWrite { get; } = new("Schema.Write");
    public static Permission SelectionsRead { get; } = new("Selections.Read");
    public static Permission SelectionsWrite { get; } = new("Selections.Write");
    public static Permission SelectionsRawSql { get; } = new("Selections.RawSql");
    public static Permission PlansRead { get; } = new("Plans.Read");
    public static Permission PlansWrite { get; } = new("Plans.Write");
    public static Permission PlansSeal { get; } = new("Plans.Seal");
    public static Permission TransfersRead { get; } = new("Transfers.Read");
    public static Permission TransfersWrite { get; } = new("Transfers.Write");
    public static Permission TransfersStart { get; } = new("Transfers.Start");
    public static Permission TransfersConstraintOverride { get; } = new("Transfers.ConstraintOverride");
    public static Permission TransfersTriggerOverride { get; } = new("Transfers.TriggerOverride");
    public static Permission TransfersUsePotentiallyLossyMapping { get; } = new("Transfers.UsePotentiallyLossyMapping");
    public static Permission AuditRead { get; } = new("Audit.Read");
    public static Permission AuditWrite { get; } = new("Audit.Write");
    public static Permission AuthProvidersManage { get; } = new("AuthProviders.Manage");
    public static Permission RoleMappingsManage { get; } = new("RoleMappings.Manage");
    public static IReadOnlyCollection<Permission> All { get; } =
        Array.AsReadOnly([
            AuditRead,
            AuditWrite,
            AuthProvidersManage,
            ConnectionsRead,
            ConnectionsWrite,
            PlansRead,
            PlansSeal,
            PlansWrite,
            RoleMappingsManage,
            SchemaRead,
            SchemaWrite,
            SelectionsRawSql,
            SelectionsRead,
            SelectionsWrite,
            TransfersConstraintOverride,
            TransfersRead,
            TransfersStart,
            TransfersTriggerOverride,
            TransfersUsePotentiallyLossyMapping,
            TransfersWrite,
        ]);
}
