using DataPitcher.Core.Closure;
using DataPitcher.Core.Plans;
namespace DataPitcher.UnitTests.Plans;
public static class PlanTestData
{
    public static readonly TableAddress Customers = new("sales", "Customers");
    public static readonly TableAddress Orders = new("sales", "Orders");
    public static TransferPlanContent Baseline(
        ConnectionFingerprint? source = null, ConnectionFingerprint? target = null,
        SchemaSnapshotReference? sourceSchema = null, SchemaSnapshotReference? targetSchema = null,
        IReadOnlyList<SelectionReference>? selections = null, IReadOnlyList<RelationshipPolicy>? relationships = null,
        IReadOnlyList<TableConflictPolicy>? conflicts = null, IReadOnlyList<StableKeyDefinition>? keys = null,
        IReadOnlyList<PlanTable>? tables = null, ConsistencyMode consistency = ConsistencyMode.FrozenKeys,
        TransferMode transfer = TransferMode.ResumableStaged, TriggerStrategy trigger = TriggerStrategy.Fire,
        ConstraintStrategy constraint = ConstraintStrategy.Enforce, BatchTarget? batch = null,
        VerificationStrategy verification = VerificationStrategy.StrictExact, ManifestCounts? totals = null) => new(
        source ?? new("PostgreSql", "source-db-001", "source-fingerprint"),
        target ?? new("PostgreSql", "target-db-001", "target-fingerprint"),
        sourceSchema ?? new("source-schema-hash"), targetSchema ?? new("target-schema-hash"),
        selections ?? [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 3, "region=EMEA"), new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 2, "status=open")],
        relationships ?? [new("FK_Orders_Customers", Orders, Customers, ["CustomerId"], ["Id"], RelationshipDirection.Outbound, true), new("FK_Orders_Customers_Alternate", Orders, Customers, ["AlternateCustomerId", "RegionId"], ["Id", "RegionId"], RelationshipDirection.Outbound, true)],
        conflicts ?? [new(Orders, RootConflictPolicy.FailOnConflict), new(Customers, RootConflictPolicy.SkipExisting)], consistency, transfer, trigger, constraint,
        keys ?? [new(Customers, "PK_Customers", ["Id"]), new(Orders, "PK_Orders", ["Id"])],
        tables ?? [Table(Orders, PlanTableState.Root), Table(Customers, PlanTableState.RequiredDependency)],
        batch ?? new(2_000, 8 * 1024 * 1024), verification, totals ?? new(2, 2, 0, 0));
    public static PlanTable Table(TableAddress table, PlanTableState state) => new(
        new TableMapping(table, new(table.Schema, table.Name), [new("Id", "Id"), new("Name", "Name")]), state,
        new ManifestCounts(1, 1, 0, 0), new TopologicalGroup([Customers, Orders]),
        CycleStrategy.NotApplicable);
    public static TransferPlanContent Reversed() => Baseline(
        selections: Baseline().Selections.Reverse().ToArray(), relationships: Baseline().Relationships.Reverse().ToArray(),
        conflicts: Baseline().ConflictPolicies.Reverse().ToArray(), keys: Baseline().StableKeys.Reverse().ToArray(),
        tables: Baseline().Tables.Reverse().Select(t => new PlanTable(
            new TableMapping(t.Mapping.Source, t.Mapping.Target, t.Mapping.Columns.Reverse().ToArray()), t.State,
            t.Manifest, new TopologicalGroup(t.TopologicalGroup.Tables.Reverse().ToArray()), t.CycleStrategy)).ToArray());
    public static TransferPlanContent Shuffled(int seed)
    {
        var random = new Random(seed); var baseline = Baseline();
        T[] Shuffle<T>(IReadOnlyList<T> values) => values.OrderBy(_ => random.Next()).ToArray();
        return Baseline(selections: Shuffle(baseline.Selections), relationships: Shuffle(baseline.Relationships), conflicts: Shuffle(baseline.ConflictPolicies), keys: Shuffle(baseline.StableKeys), tables: Shuffle(baseline.Tables).Select(t => new PlanTable(new TableMapping(t.Mapping.Source, t.Mapping.Target, Shuffle(t.Mapping.Columns)), t.State, t.Manifest, new TopologicalGroup(Shuffle(t.TopologicalGroup.Tables)), t.CycleStrategy)).ToArray());
    }
    public static TransferPlanContent CultureSensitive()
    {
        var i = new TableAddress("sales", "I"); var dottedI = new TableAddress("sales", "İ"); var orebro = new TableAddress("sales", "Orebro"); var umlaut = new TableAddress("sales", "Örebro");
        PlanTable Table(TableAddress table, PlanTableState state) => new(new(table, table, [new("Id", "Id")]), state, new(1, 1, 0, 0), new([i, dottedI, orebro, umlaut]), CycleStrategy.NotApplicable);
        return Baseline(relationships: [new("FK_Örebro_I", umlaut, i, ["Id"], ["Id"], RelationshipDirection.Outbound, true), new("FK_Orebro_İ", orebro, dottedI, ["Id"], ["Id"], RelationshipDirection.Outbound, true)], conflicts: [new(umlaut, RootConflictPolicy.FailOnConflict), new(orebro, RootConflictPolicy.SkipExisting)], keys: [new(i, "PK_I", ["Id"]), new(dottedI, "PK_İ", ["Id"]), new(orebro, "PK_Orebro", ["Id"]), new(umlaut, "PK_Örebro", ["Id"])], tables: [Table(umlaut, PlanTableState.Root), Table(orebro, PlanTableState.Root), Table(i, PlanTableState.RequiredDependency), Table(dottedI, PlanTableState.RequiredDependency)]);
    }
    public static TransferPlanContent Changed(string material) => material switch
    {
        "connection" => Baseline(source: new("PostgreSql", "source-db-001", "changed-source-fingerprint")),
        "database identity" => Baseline(target: new("PostgreSql", "changed-target-db", "target-fingerprint")),
        "schema snapshot" => Baseline(sourceSchema: new("changed-source-schema-hash")),
        "target schema snapshot" => Baseline(targetSchema: new("changed-target-schema-hash")),
        "selection" => Baseline(selections: [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 4, "region=EMEA"), new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 2, "status=open")]),
        "selection parameter" => Baseline(selections: [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 3, "region=APAC"), new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 2, "status=open")]),
        "stable key" => Baseline(keys: [new(Customers, "PK_Customers", ["Id"]), new(Orders, "UQ_Orders_External", ["ExternalId"])]),
        "relationship policy" => Baseline(relationships: [new("FK_Orders_Customers", Orders, Customers, ["CustomerId"], ["Id"], RelationshipDirection.Outbound, false), new("FK_Orders_Customers_Alternate", Orders, Customers, ["AlternateCustomerId", "RegionId"], ["Id", "RegionId"], RelationshipDirection.Outbound, true)]),
        "relationship column order" => Baseline(relationships: [new("FK_Orders_Customers", Orders, Customers, ["CustomerId"], ["Id"], RelationshipDirection.Outbound, true), new("FK_Orders_Customers_Alternate", Orders, Customers, ["RegionId", "AlternateCustomerId"], ["RegionId", "Id"], RelationshipDirection.Outbound, true)]),
        "conflict policy" => Baseline(conflicts: [new(Orders, RootConflictPolicy.Upsert), new(Customers, RootConflictPolicy.SkipExisting)]),
        "column mapping" => Baseline(tables: [new PlanTable(new TableMapping(Orders, Orders, [new("Id", "Id"), new("Name", "DisplayName")]), PlanTableState.Root, new(1, 1, 0, 0), new([Customers, Orders]), CycleStrategy.NotApplicable), Table(Customers, PlanTableState.RequiredDependency)]),
        "transfer mode" => Baseline(transfer: TransferMode.DirectFast),
        "consistency mode" => Baseline(consistency: ConsistencyMode.RepeatableReadRun),
        "trigger strategy" => Baseline(trigger: TriggerStrategy.Suppress),
        "constraint strategy" => Baseline(constraint: ConstraintStrategy.Defer),
        _ => throw new ArgumentOutOfRangeException(nameof(material)),
    };
}
