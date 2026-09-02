# DataPitcher Slice 6: Transfer Plan Construction and Sealing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a provider-free immutable transfer-plan approval artifact with deterministic canonical hashing and material-change invalidation.

**Architecture:** Core receives already-computed closure, mapping, policy, and manifest facts as immutable values; it neither opens a database connection nor queries a target. `TransferPlanContent` holds every execution-relevant value, `CanonicalPlanHasher` serializes only that content in a canonical binary representation, and `TransferPlanLifecycle` creates immutable sealed snapshots and invalidates only its active seal when the material content changes. Plan identity and presentation metadata are recorded on the sealed snapshot but excluded from its content hash so logically equivalent plans hash equally.

**Tech Stack:** .NET SDK 10.0.400, C# latest, .NET BCL (`System.Buffers`, `System.Buffers.Binary`, `System.Security.Cryptography`), xUnit, Coverlet collector, Bash.

---

## File Structure

- `src/DataPitcher.Core/Plans/TransferPlanModels.cs` — immutable provider-free plan vocabulary, content, mappings, manifest facts, and policy values.
- `src/DataPitcher.Core/Plans/CanonicalPlanHasher.cs` — versioned, ordinal, order-independent canonical byte encoding and SHA-256 plan hash.
- `src/DataPitcher.Core/Plans/TransferPlanLifecycle.cs` — draft metadata, sealed snapshot, version advancement, and material-change invalidation.
- `tests/DataPitcher.UnitTests/Plans/PlanTestData.cs` — deterministic complete logical-plan fixture and explicit material variants.
- `tests/DataPitcher.UnitTests/Plans/TransferPlanModelsTests.cs` — model coverage, defensive-copy, and downcast-indexer immutability tests.
- `tests/DataPitcher.UnitTests/Plans/CanonicalPlanHasherTests.cs` — canonical-order, culture, SCC-identifier, and generated property tests.
- `tests/DataPitcher.UnitTests/Plans/TransferPlanLifecycleTests.cs` — sealing, each required invalidator, version, and non-material-change tests.
- `scripts/test-unit.sh` — unit and architecture test lane; it remains the no-Docker developer lane.
- `scripts/test-all.sh` — the only merged line, branch, and method 100% coverage enforcement point.

## Scope and Deferrals

This slice creates the Core approval artifact only. It records source and target connection fingerprints (never connection strings or secrets), database identities, source and target schema-snapshot hashes, referenced selection versions and parameter hashes, relationship and root-conflict policy decisions, consistency and transfer modes, stable-key definitions, table and column mappings, exact per-table and aggregate manifest counts, table state, topological group membership, cycle handling, batch targets, trigger and constraint strategies, and verification strategy. `PlanTableState` must contain exactly `Root`, `RequiredDependency`, `ExplicitDependent`, `TargetSatisfied`, `Excluded`, `Blocked`, `Conflict`, and `CycleMember`.

The canonical hash is the approval artifact's content fingerprint. It covers every field of `TransferPlanContent`, including exact manifest counts and execution strategy, but intentionally excludes `PlanId`, version, creation and sealing timestamps, creator, display name, and operator note. Those fields identify or describe an approval; they do not alter what DataPitcher writes. This distinction is necessary for the equivalence property: two plans built from the same material facts must have the same hash even when different operators create them at different times. A changed material content hash invalidates the active seal and advances the version when the replacement is sealed; changing only presentation metadata leaves the existing seal current.

Canonicalization must be explicit at every unordered boundary. Use `StringComparer.Ordinal` for every string ordering and comparison. The old graph scar is directly relevant: an earlier component emitted 107 distinct raw results over 200 input shuffles, and a separate defect allowed host locale to choose string order. Do not use default `OrderBy`, `CompareTo`, interpolation for sort keys, culture-sensitive casing, or a locale-formatted value in hashing. Unordered collections are encoded as independently canonicalized item byte sequences sorted with ordinal byte order. Ordered tuples whose order is semantic — stable-key columns, relationship column pairs, and column lists — retain their declared order. Numeric and enum values are fixed-width big-endian binary values; strings are length-prefixed UTF-16 code units, avoiding locale and UTF-8 replacement-fallback ambiguities. A format tag (`DataPitcher.TransferPlan.v1`) precedes every content encoding.

Topological groups are represented by their member schema-qualified table identities, not a Tarjan `Scc.Id`, condensed-edge endpoint, traversal index, or any other SCC-derived identifier. Group membership is canonicalized by schema then table name using ordinal comparison. The code must not accept an `Scc` instance or an integer component identifier in any hashable plan type. This prevents a traversal-order assignment from changing an otherwise equivalent approval hash.

Target schema validation and conflict detection against a live database are deliberately deferred to a provider slice. Core can record a supplied `Conflict`, `Blocked`, or `TargetSatisfied` result and the selected conflict policy; it does not decide those states by probing SQL Server or PostgreSQL. This slice also defers provider capability checks, trigger/rule inspection, constraint trust validation, compatibility-matrix checks, source closure execution, persistence, job execution, target writes, and post-write verification. The recorded `VerificationStrategy` and related strategies are plan inputs, not a claim that their provider preconditions were checked here.

Core continues to depend on nothing; the architecture test enforces that boundary. No database package, provider project, filesystem persistence, Docker, or new production dependency is permitted. All slice tests run through `scripts/test-unit.sh` and need no Docker. Warnings remain errors. `scripts/test-unit.sh` reports coverage only; merged 100% line, branch, and method coverage is enforced solely by `scripts/test-all.sh`, so the final task must execute that gate after the focused tests pass.

## Tasks

### Task 1: Define immutable plan content and plan vocabulary

**Files:**
- Create: `src/DataPitcher.Core/Plans/TransferPlanModels.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Plans/PlanTestData.cs`, `tests/DataPitcher.UnitTests/Plans/TransferPlanModelsTests.cs`

- [ ] **Step 1:** Write the failing test support and model tests. `PlanTestData` below deliberately includes every model member so later hash and lifecycle tests share one complete logical plan rather than each inventing a partial one.

```csharp
// tests/DataPitcher.UnitTests/Plans/PlanTestData.cs
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
        relationships ?? [new("FK_Orders_Customers", Orders, Customers, ["CustomerId"], ["Id"], RelationshipDirection.Outbound, true), new("FK_Orders_Customers_Alternate", Orders, Customers, ["AlternateCustomerId"], ["Id"], RelationshipDirection.Outbound, true)],
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
        "selection" => Baseline(selections: [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 4, "region=EMEA"), new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 2, "status=open")]),
        "selection parameter" => Baseline(selections: [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 3, "region=APAC"), new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 2, "status=open")]),
        "stable key" => Baseline(keys: [new(Customers, "PK_Customers", ["Id"]), new(Orders, "UQ_Orders_External", ["ExternalId"])]),
        "relationship policy" => Baseline(relationships: [new("FK_Orders_Customers", Orders, Customers, ["CustomerId"], ["Id"], RelationshipDirection.Outbound, false)]),
        "conflict policy" => Baseline(conflicts: [new(Orders, RootConflictPolicy.Upsert)]),
        "column mapping" => Baseline(tables: [new PlanTable(new TableMapping(Orders, Orders, [new("Id", "Id"), new("Name", "DisplayName")]), PlanTableState.Root, new(1, 1, 0, 0), new([Customers, Orders]), CycleStrategy.NotApplicable), Table(Customers, PlanTableState.RequiredDependency)]),
        "transfer mode" => Baseline(transfer: TransferMode.DirectFast),
        "consistency mode" => Baseline(consistency: ConsistencyMode.RepeatableReadRun),
        "trigger strategy" => Baseline(trigger: TriggerStrategy.Suppress),
        "constraint strategy" => Baseline(constraint: ConstraintStrategy.Defer),
        _ => throw new ArgumentOutOfRangeException(nameof(material)),
    };
}

// tests/DataPitcher.UnitTests/Plans/TransferPlanModelsTests.cs
using DataPitcher.Core.Plans;
using Xunit;
namespace DataPitcher.UnitTests.Plans;
public sealed class TransferPlanModelsTests
{
    [Fact]
    public void TransferPlanContent_WhenSourceCollectionsChange_RetainsIndependentValues()
    {
        var columns = new List<ColumnMapping> { new("Id", "Id") };
        var group = new List<TableAddress> { PlanTestData.Customers };
        var table = new PlanTable(new(PlanTestData.Customers, PlanTestData.Customers, columns), PlanTableState.Root, new(1, 1, 0, 0), new(group), CycleStrategy.NotApplicable);
        var tables = new List<PlanTable> { table };
        var content = PlanTestData.Baseline(tables: tables);
        columns[0] = new("Id", "Changed"); group[0] = PlanTestData.Orders; tables[0] = PlanTestData.Table(PlanTestData.Orders, PlanTableState.Conflict);
        Assert.Equal("Id", content.Tables[0].Mapping.Columns[0].Target);
        Assert.Equal(PlanTestData.Customers, content.Tables[0].TopologicalGroup.Tables[0]);
        Assert.Equal(PlanTableState.Root, content.Tables[0].State);
    }
    [Fact]
    public void TransferPlanContent_WhenExposedCollectionsAreDowncast_RejectsIndexerAssignment()
    {
        var content = PlanTestData.Baseline(); var table = content.Tables[0];
        Assert.Throws<NotSupportedException>(() => ((IList<PlanTable>)content.Tables)[0] = table);
        Assert.Throws<NotSupportedException>(() => ((IList<ColumnMapping>)table.Mapping.Columns)[0] = new("Id", "Changed"));
        Assert.Throws<NotSupportedException>(() => ((IList<TableAddress>)table.TopologicalGroup.Tables)[0] = PlanTestData.Orders);
        Assert.Throws<NotSupportedException>(() => ((IList<SelectionReference>)content.Selections)[0] = content.Selections[0]);
    }
    [Theory]
    [InlineData(PlanTableState.Root)] [InlineData(PlanTableState.RequiredDependency)]
    [InlineData(PlanTableState.ExplicitDependent)] [InlineData(PlanTableState.TargetSatisfied)]
    [InlineData(PlanTableState.Excluded)] [InlineData(PlanTableState.Blocked)]
    [InlineData(PlanTableState.Conflict)] [InlineData(PlanTableState.CycleMember)]
    public void PlanTable_RecordsEveryDefinedState(PlanTableState state) => Assert.Equal(state, PlanTestData.Table(PlanTestData.Orders, state).State);
}
```

- [ ] **Step 2:** Run `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~TransferPlanModelsTests"` and confirm compilation fails with CS0234: `The type or namespace name 'Plans' does not exist in the namespace 'DataPitcher.Core'`.

- [ ] **Step 3:** Write the minimal immutable Core model. Use `Array.AsReadOnly(values.ToArray())` for every public collection, including nested mapping columns and topological-group members; `IReadOnlyList<T>` alone is insufficient because callers can downcast a supplied `List<T>`. The model intentionally contains no live-provider validation.

```csharp
// src/DataPitcher.Core/Plans/TransferPlanModels.cs
using DataPitcher.Core.Closure;
namespace DataPitcher.Core.Plans;
public enum PlanTableState { Root, RequiredDependency, ExplicitDependent, TargetSatisfied, Excluded, Blocked, Conflict, CycleMember }
public enum RelationshipDirection { Outbound, Inbound }
public enum ConsistencyMode { FrozenKeys, RepeatableReadRun }
public enum TransferMode { DirectFast, ResumableStaged, ServerSide }
public enum CycleStrategy { NotApplicable, DeferredConstraints, NullableForeignKeyTwoPhase, SuspendAndRevalidateConstraints, Blocked }
public enum TriggerStrategy { Fire, Suppress }
public enum ConstraintStrategy { Enforce, Defer, DisableAndRevalidate }
public enum VerificationStrategy { Standard, StrictExact }
public sealed record TableAddress(string Schema, string Name);
public sealed record ConnectionFingerprint(string Provider, string DatabaseIdentity, string Fingerprint);
public sealed record SchemaSnapshotReference(string Hash);
public sealed record SelectionReference(Guid SelectionId, long Version, string ParameterHash);
public sealed class RelationshipPolicy
{
    public RelationshipPolicy(string name, TableAddress from, TableAddress to, IEnumerable<string> fromColumns, IEnumerable<string> toColumns, RelationshipDirection direction, bool isEnabled)
    { Name = name; From = from; To = to; FromColumns = Array.AsReadOnly(fromColumns.ToArray()); ToColumns = Array.AsReadOnly(toColumns.ToArray()); Direction = direction; IsEnabled = isEnabled; }
    public string Name { get; } public TableAddress From { get; } public TableAddress To { get; } public IReadOnlyList<string> FromColumns { get; } public IReadOnlyList<string> ToColumns { get; } public RelationshipDirection Direction { get; } public bool IsEnabled { get; }
}
public sealed record TableConflictPolicy(TableAddress Table, RootConflictPolicy Policy);
public sealed class StableKeyDefinition
{
    public StableKeyDefinition(TableAddress table, string constraintName, IEnumerable<string> columns) { Table = table; ConstraintName = constraintName; Columns = Array.AsReadOnly(columns.ToArray()); }
    public TableAddress Table { get; } public string ConstraintName { get; } public IReadOnlyList<string> Columns { get; }
}
public sealed record ColumnMapping(string Source, string Target);
public sealed class TableMapping
{
    public TableMapping(TableAddress source, TableAddress target, IEnumerable<ColumnMapping> columns) { Source = source; Target = target; Columns = Array.AsReadOnly(columns.ToArray()); }
    public TableAddress Source { get; } public TableAddress Target { get; } public IReadOnlyList<ColumnMapping> Columns { get; }
}
public sealed record ManifestCounts(long Included, long PlannedWrites, long Inserts, long Updates);
public sealed class TopologicalGroup
{
    public TopologicalGroup(IEnumerable<TableAddress> tables) => Tables = Array.AsReadOnly(tables.ToArray());
    public IReadOnlyList<TableAddress> Tables { get; }
}
public sealed class PlanTable
{
    public PlanTable(TableMapping mapping, PlanTableState state, ManifestCounts manifest, TopologicalGroup topologicalGroup, CycleStrategy cycleStrategy) { Mapping = mapping; State = state; Manifest = manifest; TopologicalGroup = topologicalGroup; CycleStrategy = cycleStrategy; }
    public TableMapping Mapping { get; } public PlanTableState State { get; } public ManifestCounts Manifest { get; } public TopologicalGroup TopologicalGroup { get; } public CycleStrategy CycleStrategy { get; }
}
public sealed class TransferPlanContent
{
    public TransferPlanContent(ConnectionFingerprint source, ConnectionFingerprint target, SchemaSnapshotReference sourceSchema, SchemaSnapshotReference targetSchema, IEnumerable<SelectionReference> selections, IEnumerable<RelationshipPolicy> relationships, IEnumerable<TableConflictPolicy> conflictPolicies, ConsistencyMode consistencyMode, TransferMode transferMode, TriggerStrategy triggerStrategy, ConstraintStrategy constraintStrategy, IEnumerable<StableKeyDefinition> stableKeys, IEnumerable<PlanTable> tables, BatchTarget batchTarget, VerificationStrategy verificationStrategy, ManifestCounts manifestTotals)
    { Source = source; Target = target; SourceSchema = sourceSchema; TargetSchema = targetSchema; Selections = Array.AsReadOnly(selections.ToArray()); Relationships = Array.AsReadOnly(relationships.ToArray()); ConflictPolicies = Array.AsReadOnly(conflictPolicies.ToArray()); ConsistencyMode = consistencyMode; TransferMode = transferMode; TriggerStrategy = triggerStrategy; ConstraintStrategy = constraintStrategy; StableKeys = Array.AsReadOnly(stableKeys.ToArray()); Tables = Array.AsReadOnly(tables.ToArray()); BatchTarget = batchTarget; VerificationStrategy = verificationStrategy; ManifestTotals = manifestTotals; }
    public ConnectionFingerprint Source { get; } public ConnectionFingerprint Target { get; } public SchemaSnapshotReference SourceSchema { get; } public SchemaSnapshotReference TargetSchema { get; } public IReadOnlyList<SelectionReference> Selections { get; } public IReadOnlyList<RelationshipPolicy> Relationships { get; } public IReadOnlyList<TableConflictPolicy> ConflictPolicies { get; } public ConsistencyMode ConsistencyMode { get; } public TransferMode TransferMode { get; } public TriggerStrategy TriggerStrategy { get; } public ConstraintStrategy ConstraintStrategy { get; } public IReadOnlyList<StableKeyDefinition> StableKeys { get; } public IReadOnlyList<PlanTable> Tables { get; } public BatchTarget BatchTarget { get; } public VerificationStrategy VerificationStrategy { get; } public ManifestCounts ManifestTotals { get; }
}
public sealed record BatchTarget(int MaximumRows, int MaximumBytes);
```

- [ ] **Step 4:** Run `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~TransferPlanModelsTests"` and confirm all model tests pass with `Failed: 0`.

- [ ] **Step 5:** Commit with `git add src/DataPitcher.Core/Plans/TransferPlanModels.cs tests/DataPitcher.UnitTests/Plans/PlanTestData.cs tests/DataPitcher.UnitTests/Plans/TransferPlanModelsTests.cs && git commit -m "feat: model immutable transfer plans"`.

### Task 2: Canonicalize and hash all material plan content

**Files:**
- Create: `src/DataPitcher.Core/Plans/CanonicalPlanHasher.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Plans/CanonicalPlanHasherTests.cs`

- [ ] **Step 1:** Write the failing canonical-hash tests. The generated cases are deterministic property-based tests: each seed creates a distinct ordering of the same logical plan, so a failure identifies a reproducible seed without a package dependency. The material-change property uses every required material category; lifecycle tests in Task 3 separately prove invalidation and version behavior for every category.

```csharp
using System.Globalization;
using DataPitcher.Core.Plans;
using Xunit;
namespace DataPitcher.UnitTests.Plans;
public sealed class CanonicalPlanHasherTests
{
    [Fact]
    public void Hash_WhenEveryCollectionIsReversed_IsIdentical() => Assert.Equal(CanonicalPlanHasher.Hash(PlanTestData.Baseline()), CanonicalPlanHasher.Hash(PlanTestData.Reversed()));
    [Fact]
    public void Hash_WhenCurrentCultureIsSwedishOrTurkish_IsUnchanged()
    {
        var originalCulture = CultureInfo.CurrentCulture; var originalUiCulture = CultureInfo.CurrentUICulture; var content = PlanTestData.CultureSensitive(); var expected = CanonicalPlanHasher.Hash(content);
        try { foreach (var name in new[] { "sv-SE", "tr-TR" }) { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name); Assert.Equal(expected, CanonicalPlanHasher.Hash(content)); } }
        finally { CultureInfo.CurrentCulture = originalCulture; CultureInfo.CurrentUICulture = originalUiCulture; }
    }
    [Fact]
    public void Hash_WhenTopologicalGroupMemberInputOrderDiffers_IsIdentical()
    {
        var first = PlanTestData.Baseline(); var second = PlanTestData.Reversed();
        Assert.Equal(CanonicalPlanHasher.Hash(first), CanonicalPlanHasher.Hash(second));
    }
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 100).Select(seed => new object[] { seed });
    [Theory] [MemberData(nameof(Seeds))]
    public void Property_EquivalentCanonicalPlans_HashEqually(int seed)
    {
        var content = PlanTestData.Baseline(); var shuffled = PlanTestData.Shuffled(seed);
        Assert.Equal(CanonicalPlanHasher.Hash(content), CanonicalPlanHasher.Hash(shuffled));
    }
    public static IEnumerable<object[]> MaterialChanges() => new[] { "connection", "database identity", "schema snapshot", "selection", "selection parameter", "stable key", "relationship policy", "conflict policy", "column mapping", "transfer mode", "consistency mode", "trigger strategy", "constraint strategy" }.Select(value => new object[] { value });
    [Theory] [MemberData(nameof(MaterialChanges))]
    public void Property_AnyMaterialChange_ChangesHash(string material) => Assert.NotEqual(CanonicalPlanHasher.Hash(PlanTestData.Baseline()), CanonicalPlanHasher.Hash(PlanTestData.Changed(material)));
}
```

- [ ] **Step 2:** Run `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~CanonicalPlanHasherTests"` and confirm compilation fails with CS0103: `The name 'CanonicalPlanHasher' does not exist in the current context`.

- [ ] **Step 3:** Implement a single canonical binary encoder and SHA-256 hasher. Every `Unordered` call first encodes an individual item, sorts its bytes with ordinal byte ordering, then appends the sorted bytes. Every `Ordered` call preserves supplied sequence order. The `TopologicalGroup` writer takes only table identities; no model type or writer receives `Scc.Id`.

```csharp
// src/DataPitcher.Core/Plans/CanonicalPlanHasher.cs
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
namespace DataPitcher.Core.Plans;
public static class CanonicalPlanHasher
{
    public static string Hash(TransferPlanContent plan)
    {
        var w = new Writer(); w.Text("DataPitcher.TransferPlan.v1");
        Connection(w, plan.Source); Connection(w, plan.Target); w.Text(plan.SourceSchema.Hash); w.Text(plan.TargetSchema.Hash);
        Unordered(w, plan.Selections, Selection); Unordered(w, plan.Relationships, Relationship); Unordered(w, plan.ConflictPolicies, Conflict);
        w.Int((int)plan.ConsistencyMode); w.Int((int)plan.TransferMode); w.Int((int)plan.TriggerStrategy); w.Int((int)plan.ConstraintStrategy);
        Unordered(w, plan.StableKeys, StableKey); Unordered(w, plan.Tables, Table); Batch(w, plan.BatchTarget); w.Int((int)plan.VerificationStrategy); Counts(w, plan.ManifestTotals);
        return Convert.ToHexString(SHA256.HashData(w.Bytes));
    }
    private static void Connection(Writer w, ConnectionFingerprint x) { w.Text(x.Provider); w.Text(x.DatabaseIdentity); w.Text(x.Fingerprint); }
    private static void Selection(Writer w, SelectionReference x) { w.Text(x.SelectionId.ToString("D")); w.Long(x.Version); w.Text(x.ParameterHash); }
    private static void Relationship(Writer w, RelationshipPolicy x) { w.Text(x.Name); Address(w, x.From); Address(w, x.To); Ordered(w, x.FromColumns, (a, v) => a.Text(v)); Ordered(w, x.ToColumns, (a, v) => a.Text(v)); w.Int((int)x.Direction); w.Bool(x.IsEnabled); }
    private static void Conflict(Writer w, TableConflictPolicy x) { Address(w, x.Table); w.Int((int)x.Policy); }
    private static void StableKey(Writer w, StableKeyDefinition x) { Address(w, x.Table); w.Text(x.ConstraintName); Ordered(w, x.Columns, (a, v) => a.Text(v)); }
    private static void Table(Writer w, PlanTable x) { Mapping(w, x.Mapping); w.Int((int)x.State); Counts(w, x.Manifest); Group(w, x.TopologicalGroup); w.Int((int)x.CycleStrategy); }
    private static void Mapping(Writer w, TableMapping x) { Address(w, x.Source); Address(w, x.Target); Unordered(w, x.Columns, (a, v) => { a.Text(v.Source); a.Text(v.Target); }); }
    private static void Group(Writer w, TopologicalGroup x) => Unordered(w, x.Tables, Address);
    private static void Batch(Writer w, BatchTarget x) { w.Int(x.MaximumRows); w.Int(x.MaximumBytes); }
    private static void Counts(Writer w, ManifestCounts x) { w.Long(x.Included); w.Long(x.PlannedWrites); w.Long(x.Inserts); w.Long(x.Updates); }
    private static void Address(Writer w, TableAddress x) { w.Text(x.Schema); w.Text(x.Name); }
    private static void Ordered<T>(Writer w, IEnumerable<T> values, Action<Writer, T> item) { var all = values.ToArray(); w.Int(all.Length); foreach (var value in all) item(w, value); }
    private static void Unordered<T>(Writer w, IEnumerable<T> values, Action<Writer, T> item)
    {
        var all = values.Select(value => { var nested = new Writer(); item(nested, value); return nested.Bytes.ToArray(); }).OrderBy(bytes => Convert.ToHexString(bytes), StringComparer.Ordinal).ToArray();
        w.Int(all.Length); foreach (var value in all) w.Raw(value);
    }
    private sealed class Writer
    {
        private readonly ArrayBufferWriter<byte> _buffer = new(); public ReadOnlySpan<byte> Bytes => _buffer.WrittenSpan;
        public void Bool(bool value) => Int(value ? 1 : 0);
        public void Int(int value) { var span = _buffer.GetSpan(4); BinaryPrimitives.WriteInt32BigEndian(span, value); _buffer.Advance(4); }
        public void Long(long value) { var span = _buffer.GetSpan(8); BinaryPrimitives.WriteInt64BigEndian(span, value); _buffer.Advance(8); }
        public void Text(string value) { Int(value.Length); foreach (var character in value) { var span = _buffer.GetSpan(2); BinaryPrimitives.WriteUInt16BigEndian(span, character); _buffer.Advance(2); } }
        public void Raw(ReadOnlySpan<byte> value) { var span = _buffer.GetSpan(value.Length); value.CopyTo(span); _buffer.Advance(value.Length); }
    }
}
```

- [ ] **Step 4:** Run `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~CanonicalPlanHasherTests"` and confirm all canonical, culture, SCC-member, and generated property cases pass with `Failed: 0`.

- [ ] **Step 5:** Commit with `git add src/DataPitcher.Core/Plans/CanonicalPlanHasher.cs tests/DataPitcher.UnitTests/Plans/CanonicalPlanHasherTests.cs && git commit -m "feat: add canonical transfer plan hashing"`.

### Task 3: Seal plans and invalidate only material revisions

**Files:**
- Create: `src/DataPitcher.Core/Plans/TransferPlanLifecycle.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Plans/TransferPlanLifecycleTests.cs`

- [ ] **Step 1:** Write the failing lifecycle tests. Each material category gets a separately named fact: a parameterized case would not distinguish a missing invalidator from an implementation that invalidates everything. The non-material display-name fact is the necessary discriminator against that incorrect implementation.

```csharp
using DataPitcher.Core.Plans;
using Xunit;
namespace DataPitcher.UnitTests.Plans;
public sealed class TransferPlanLifecycleTests
{
    [Fact] public void Plan_WhenConnectionChanges_InvalidatesSeal() => AssertInvalidates("connection");
    [Fact] public void Plan_WhenDatabaseIdentityChanges_InvalidatesSeal() => AssertInvalidates("database identity");
    [Fact] public void Plan_WhenSchemaSnapshotChanges_InvalidatesSeal() => AssertInvalidates("schema snapshot");
    [Fact] public void Plan_WhenSelectionChanges_InvalidatesSeal() => AssertInvalidates("selection");
    [Fact] public void Plan_WhenSelectionParameterChanges_InvalidatesSeal() => AssertInvalidates("selection parameter");
    [Fact] public void Plan_WhenStableKeyDefinitionChanges_InvalidatesSeal() => AssertInvalidates("stable key");
    [Fact] public void Plan_WhenRelationshipPolicyChanges_InvalidatesSeal() => AssertInvalidates("relationship policy");
    [Fact] public void Plan_WhenConflictPolicyChanges_InvalidatesSeal() => AssertInvalidates("conflict policy");
    [Fact] public void Plan_WhenColumnMappingChanges_InvalidatesSeal() => AssertInvalidates("column mapping");
    [Fact] public void Plan_WhenTransferModeChanges_InvalidatesSeal() => AssertInvalidates("transfer mode");
    [Fact] public void Plan_WhenConsistencyModeChanges_InvalidatesSeal() => AssertInvalidates("consistency mode");
    [Fact] public void Plan_WhenTriggerStrategyChanges_InvalidatesSeal() => AssertInvalidates("trigger strategy");
    [Fact] public void Plan_WhenConstraintStrategyChanges_InvalidatesSeal() => AssertInvalidates("constraint strategy");
    [Fact]
    public void Plan_WhenOnlyDisplayNameChanges_DoesNotInvalidateSeal()
    {
        var lifecycle = new TransferPlanLifecycle(new("First label", "note", "operator-a", new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero), PlanTestData.Baseline()));
        var sealedPlan = lifecycle.Seal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new DateTimeOffset(2026, 9, 2, 11, 0, 0, TimeSpan.Zero));
        lifecycle.Replace(new("Second label", "note", "operator-a", new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero), PlanTestData.Baseline()));
        Assert.Same(sealedPlan, lifecycle.CurrentSeal); Assert.Equal(1, lifecycle.CurrentSeal!.Identity.Version);
    }
    [Fact]
    public void SealedPlan_WhenCollectionsAreDowncast_RejectsIndexerAssignment()
    {
        var lifecycle = new TransferPlanLifecycle(new("Label", null, "operator-a", DateTimeOffset.UnixEpoch, PlanTestData.Baseline()));
        var sealedPlan = lifecycle.Seal(Guid.Empty, DateTimeOffset.UnixEpoch);
        Assert.Throws<NotSupportedException>(() => ((IList<PlanTable>)sealedPlan.Content.Tables)[0] = sealedPlan.Content.Tables[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<ColumnMapping>)sealedPlan.Content.Tables[0].Mapping.Columns)[0] = new("Id", "Changed"));
    }
    private static void AssertInvalidates(string material)
    {
        var lifecycle = new TransferPlanLifecycle(new("Label", null, "operator-a", DateTimeOffset.UnixEpoch, PlanTestData.Baseline()));
        var before = lifecycle.Seal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), DateTimeOffset.UnixEpoch);
        lifecycle.Replace(new("Label", null, "operator-a", DateTimeOffset.UnixEpoch, PlanTestData.Changed(material)));
        Assert.Null(lifecycle.CurrentSeal);
        var after = lifecycle.Seal(before.Identity.PlanId, DateTimeOffset.UnixEpoch.AddMinutes(1));
        Assert.Equal(before.Identity.Version + 1, after.Identity.Version); Assert.NotEqual(before.CanonicalHash, after.CanonicalHash);
    }
}
```

- [ ] **Step 2:** Run `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~TransferPlanLifecycleTests"` and confirm compilation fails with CS0246: `The type or namespace name 'TransferPlanLifecycle' could not be found`.

- [ ] **Step 3:** Implement the lifecycle. A sealed instance has no setters, retains the already-defensive `TransferPlanContent`, and receives its hash once. `Replace` compares canonical content hashes with `StringComparer.Ordinal`; only a material replacement while a seal is current clears it and increments the next sealing version. It must not mutate the old sealed instance.

```csharp
// src/DataPitcher.Core/Plans/TransferPlanLifecycle.cs
namespace DataPitcher.Core.Plans;
public sealed class TransferPlanDraft
{
    public TransferPlanDraft(string displayName, string? operatorNote, string createdBy, DateTimeOffset createdAtUtc, TransferPlanContent content) { DisplayName = displayName; OperatorNote = operatorNote; CreatedBy = createdBy; CreatedAtUtc = createdAtUtc; Content = content; }
    public string DisplayName { get; } public string? OperatorNote { get; } public string CreatedBy { get; } public DateTimeOffset CreatedAtUtc { get; } public TransferPlanContent Content { get; }
}
public sealed record TransferPlanIdentity(Guid PlanId, int Version, DateTimeOffset CreatedAtUtc, DateTimeOffset SealedAtUtc, string CreatedBy);
public sealed class SealedTransferPlan
{
    public SealedTransferPlan(TransferPlanIdentity identity, TransferPlanContent content, string canonicalHash) { Identity = identity; Content = content; CanonicalHash = canonicalHash; }
    public TransferPlanIdentity Identity { get; } public TransferPlanContent Content { get; } public string CanonicalHash { get; }
}
public sealed class TransferPlanLifecycle
{
    private int _nextVersion = 1;
    public TransferPlanLifecycle(TransferPlanDraft draft) => Draft = draft;
    public TransferPlanDraft Draft { get; private set; }
    public SealedTransferPlan? CurrentSeal { get; private set; }
    public void Replace(TransferPlanDraft draft)
    {
        var changed = !StringComparer.Ordinal.Equals(CanonicalPlanHasher.Hash(Draft.Content), CanonicalPlanHasher.Hash(draft.Content));
        Draft = draft;
        if (changed && CurrentSeal is not null) { CurrentSeal = null; _nextVersion++; }
    }
    public SealedTransferPlan Seal(Guid planId, DateTimeOffset sealedAtUtc)
    {
        CurrentSeal ??= new SealedTransferPlan(new(planId, _nextVersion, Draft.CreatedAtUtc, sealedAtUtc, Draft.CreatedBy), Draft.Content, CanonicalPlanHasher.Hash(Draft.Content));
        return CurrentSeal;
    }
}
```

- [ ] **Step 4:** Run `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~TransferPlanLifecycleTests"` and confirm all 15 lifecycle tests pass with `Failed: 0`.

- [ ] **Step 5:** Run `scripts/test-unit.sh` and confirm the Core unit and architecture suites pass without Docker.

- [ ] **Step 6:** Run `scripts/test-all.sh` and confirm the output reports `Merged coverage: line=100% branch=100% method=100%` with exit code 0.

- [ ] **Step 7:** Commit with `git add src/DataPitcher.Core/Plans/TransferPlanLifecycle.cs tests/DataPitcher.UnitTests/Plans/TransferPlanLifecycleTests.cs && git commit -m "feat: seal and invalidate transfer plans"`.

## Self-Review

- [ ] Confirm coverage includes all required plan fields: identity/version/timestamps/creator on the sealed artifact; both connection fingerprints and database identities; source and target snapshot hashes; selection versions and parameter hashes; relationship and conflict policies; consistency and transfer modes; stable keys; table and column mappings; exact manifest counts; all eight table states; topological-group members; cycle, trigger, constraint, batch, and verification strategies; and the canonical hash.
- [ ] Confirm canonical tests reverse every unordered collection, force `sv-SE` and `tr-TR`, use 100 deterministic generated equivalent cases, prove every named material change changes the hash, and never serialize an SCC identifier or use a non-ordinal comparer.
- [ ] Confirm lifecycle tests contain one named invalidation test per required material change, prove a display-name-only change retains the same seal, assert version advancement after resealing, and assign through downcast collection indexers on sealed data rather than relying on removal failures.
- [ ] Confirm target schema validation, live target conflict detection, provider capabilities, database I/O, execution, persistence, and verification implementation remain deferred to provider or later slices; Core adds no dependency.
- [ ] Check every type and method name is consistent across Tasks 1–3, the fixture, commands, and commit paths: `TransferPlanContent`, `CanonicalPlanHasher.Hash`, `TransferPlanDraft`, `TransferPlanLifecycle.Replace`, `TransferPlanLifecycle.Seal`, `SealedTransferPlan`, and `CurrentSeal`.
