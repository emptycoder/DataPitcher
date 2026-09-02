# DataPitcher Slice 7: Cycle Detection and Strategy Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add provider-neutral row-graph analysis contracts and select one safe cycle strategy per planned table component without executing database writes.

**Architecture:** The dependency graph continues to identify table-level SCCs, while this slice introduces a row-level contract scoped to one planned component. A future provider stages its planned keys and references, returns a bounded set-based analysis, and the dependency-free Core selector tests the unsuppressed row graph before each single-strategy candidate edge set. The selector never performs per-row traversal or executes a load; it only consumes analysis results and provider-supplied capability facts.

**Tech Stack:** .NET 10, C# latest, xUnit, Coverlet collector, Bash test lanes, existing Core schema/identity/closure models.

---

## File Structure

- `src/DataPitcher.Core/Graph/RowGraph.cs` — immutable row-reference request/result contracts, bounded set-based analyzer seam, and cycle-analysis facts.
- `src/DataPitcher.Core/Graph/CycleStrategySelector.cs` — five-outcome, edge-set-based provider-neutral strategy selection.
- `tests/DataPitcher.UnitTests/Graph/RowGraphModelsTests.cs` — row reference state, missing-reference, immutability, and analyzer-contract model tests.
- `tests/DataPitcher.UnitTests/Graph/CycleStrategySelectorTests.cs` — scripted analyzer fixtures for ordered forests, real cycles, candidate residual graphs, and blocked diagnostics.
- `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs` — assertion that Core has no project or package dependencies.
- `docs/plans/2026-09-02-slice-7-cycle-strategy-selection.md` — this implementation plan.

## Scope and Deferrals

`DependencyGraph`, `TarjanScc`, and `CondensedGraph` remain table-level topology only. An SCC is not evidence of a planned-row cycle: `employees.manager_id -> employees.id` gives a one-table SCC even when its selected employee rows form an ordinary forest. The first selector action is therefore always the unsuppressed row-graph analysis. If every planned row is reached in parent-before-child order, return `Ordered`, preserve every foreign key, and use the supplied row order. This is the common outcome; do not subject an acyclic employee hierarchy to any special cycle strategy.

`ISetBasedRowGraphAnalyzer` is the provider-neutral boundary for the required detection. A provider bulk-stages planned stable keys and relationship references; no application code may issue one query or one traversal step per row. For one self-referencing relationship, the provider uses a recursive CTE seeded by NULL-parent and target-satisfied-parent roots, assigns `parent level + 1`, and returns ascending levels. For multiple relationships, it uses set-based Kahn frontier removal: each bulk frontier removes all rows whose remaining planned parent references are satisfied, then produces the next frontier in a set operation. Rows left after the frontier is empty are a genuine row cycle. Both forms are O(V+E) time and storage, where V is staged planned rows and E is planned references, but the provider must enforce the request's maximum recursion level, command timeout, and temporary-space ceiling.

`NullParent` is a satisfied root. `TargetSatisfied` means the parent is outside the plan and already exists behind the required enforced/trusted target constraint, so it is also a root. `Missing` is neither a row-graph edge nor a fabricated row: it is returned as `MissingReference` with the child, parent key, and relationship. Missing references travel separately with an otherwise acyclic result so the higher plan validator can reject them without inventing a sixth cycle strategy.

The selector considers exactly five outcomes in order: `Ordered`; PostgreSQL-only `Deferred`; `NullableTwoPhase`; `ConstraintSuspension`; then derived `Blocked`. Deferred is eligible only if the provider declares support and every candidate foreign key is already enforced, validated, and declared deferrable; SQL Server supplies `SupportsDeferrableForeignKeys = false` and has no equivalent. Nullable two-phase eligibility is provider-proven, not column-nullability alone: temporary NULLs must be safe under MATCH semantics, CHECK constraints, triggers, and one target transaction. Suspension eligibility includes authorization, durable recovery, and global revalidation readiness. For each candidate, remove that strategy's eligible foreign-key edge set and ask the analyzer whether the residual row graph orders. A strategy wins only if its own edge set breaks every cycle. Do not union a nullable set with a suspension set or otherwise combine strategies inside one SCC. `MustBlockScc` is a derived property of the selection when none succeeds, not a provider feature.

This slice deliberately excludes provider SQL/CTE implementation, staging tables, capability catalog probes, transactions, inserts, temporary NULL updates, constraint disabling, recovery journals, revalidation, and all strategy execution. Those require provider-specific database slices. Core must remain free of project and package dependencies; the architecture test enforces that boundary. Add unit and architecture tests only, run them through `scripts/test-unit.sh`, and require no Docker. Warnings remain errors. One hundred percent line, branch, and method coverage is merged and enforced only by `scripts/test-all.sh`; do not add a per-lane coverage gate.

The component request contains exact `RowAddress` values, not table names or concatenated keys. `RowAddress` reuses the existing `TableDefinition` identity and ordered, native-typed `StableKey`, so a composite key remains a composite key and two foreign keys between the same tables remain distinct `ForeignKeyDefinition` values. The caller supplies only the rows planned for the current table SCC. A relationship whose parent is another planned row remains a residual graph edge unless the selector passes that relationship in the current candidate exclusion set. A `TargetSatisfied` or `Missing` parent must not be reclassified simply because a table with the same name belongs to the SCC; the distinction is about the exact planned row and its target existence.

The analyzer result is deliberately richer than a Boolean. `InsertionOrder` is meaningful only when `UnreachedRows` is empty and must place every required planned parent before its child. `UnreachedRows` identifies the rows participating in or trapped behind a cyclic residual subgraph, which lets a later provider diagnostic show useful keys without exposing application-side traversal. `MissingReferences` remains populated whether or not a cycle also exists; selector strategy choice does not hide a data-validity error. The caller must pass the same frozen plan snapshot to the baseline and every residual analysis so a candidate result cannot be compared against a changed row set.

Capability facts are conjunctive across the selected edge set. A provider must mark a deferred edge eligible only when PostgreSQL can defer that already-declared constraint; it must not suggest `ALTER TABLE` to change definition-time metadata. It must mark a nullable edge eligible only when all temporary NULL columns are nullable and the entire insert/update/validation/commit sequence is transactionally safe. It must mark a suspension edge eligible only when every affected table can be suspended, recovered, and globally revalidated. The selector deliberately asks one residual question per outcome rather than optimizing a minimum edge set: the product needs the first safe supported strategy in ADR order, not a speculative optimizer.

The unit fake is confined to selection semantics. It asserts that `Ordered` calls the analyzer exactly once with no exclusions; that each later attempt supplies all and only the eligible foreign keys for that one strategy; and that a failed nullable attempt cannot be combined with a later suspension attempt. Provider slices must add database-backed fixtures for the physical evidence absent here: roots reached through NULL and target rows, missing-parent extraction, recursive depth exhaustion, timeout and staging-space limit failures, and multiple-reference frontier removal. They must also prove ordered key materialization and provider-native comparison for composite keys. None of that database access belongs in Core or this Docker-free lane.

### Task 1: Define bounded row-graph contracts and the Core boundary

**Files:**
- Create: `src/DataPitcher.Core/Graph/RowGraph.cs`
- Modify: `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs`
- Test: `tests/DataPitcher.UnitTests/Graph/RowGraphModelsTests.cs`, `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs`

- [ ] **Step 1: Write the failing row-graph model and Core-dependency tests.**

`tests/DataPitcher.UnitTests/Graph/RowGraphModelsTests.cs`:

```csharp
using DataPitcher.Core.Closure;
using DataPitcher.Core.Graph;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Graph;

public sealed class RowGraphModelsTests
{
    [Fact]
    public void RowGraphRequest_WhenInputsAreMutated_ExposesImmutableRowsAndReferences()
    {
        var employees = Table("Employees"); var fk = ForeignKey(employees, employees);
        var manager = Row(employees, 1); var employee = Row(employees, 2);
        var rows = new List<RowAddress> { manager, employee };
        var references = new List<RowReference>
        {
            new(manager, fk, null, RowReferenceState.NullParent),
            new(employee, fk, manager, RowReferenceState.Planned),
        };
        var request = new RowGraphRequest(rows, references, new(10, TimeSpan.FromSeconds(5), 1_000_000));
        rows.Clear(); references.Clear();
        Assert.Equal(2, request.PlannedRows.Count); Assert.Equal(2, request.References.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<RowAddress>)request.PlannedRows).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<RowReference>)request.References).Clear());
    }

    [Fact]
    public void RowGraphAnalysis_WhenExternalParentIsMissing_ReportsItSeparatelyFromAcyclicOrder()
    {
        var employees = Table("Employees"); var fk = ForeignKey(employees, employees);
        var employee = Row(employees, 2); var absentManager = Row(employees, 99);
        var analysis = new RowGraphAnalysis([employee], [], [new(employee, fk, absentManager)]);
        Assert.True(analysis.IsAcyclic); Assert.Empty(analysis.UnreachedRows);
        Assert.Equal(new MissingReference(employee, fk, absentManager), Assert.Single(analysis.MissingReferences));
    }

    private static TableDefinition Table(string name) => new("dbo", name, [], new($"PK_{name}", ["Id"]), []);
    private static ForeignKeyDefinition ForeignKey(TableDefinition child, TableDefinition parent) => new($"FK_{child.Name}_{parent.Name}", child, parent, ["ManagerId"], ["Id"], true, true);
    private static RowAddress Row(TableDefinition table, int id) => new(table, new StableKey([new("Id", id)]));
}
```

Add this fact to the existing `DependencyRuleTests` class:

```csharp
[Fact]
public void Core_HasNoProjectOrPackageDependencies()
{
    var core = Project("DataPitcher.Core");
    Assert.Empty(References(core));
    Assert.Empty(Packages(core));
}
```

- [ ] **Step 2: Run the focused lane and confirm the contracts are absent.**

Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~RowGraphModelsTests|FullyQualifiedName~Core_HasNoProjectOrPackageDependencies"`

Expected: compilation fails before test execution with CS0246 stating that `RowReference` could not be found, because `RowGraph.cs` has not been created. The new XML-only architecture assertion is expected to be valid once compilation reaches it.

- [ ] **Step 3: Implement the immutable contracts and set-based analyzer seam.**

`src/DataPitcher.Core/Graph/RowGraph.cs`:

```csharp
using DataPitcher.Core.Closure;
using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Graph;

public enum RowReferenceState { NullParent, Planned, TargetSatisfied, Missing }

public sealed record RowReference(RowAddress Child, ForeignKeyDefinition Relationship, RowAddress? Parent, RowReferenceState State);

public sealed record MissingReference(RowAddress Child, ForeignKeyDefinition Relationship, RowAddress Parent);

public sealed record RowGraphLimits(int MaximumRecursionLevels, TimeSpan CommandTimeout, long MaximumTemporaryBytes);

public sealed class RowGraphRequest
{
    public RowGraphRequest(IEnumerable<RowAddress> plannedRows, IEnumerable<RowReference> references, RowGraphLimits limits)
    {
        PlannedRows = Array.AsReadOnly(plannedRows.ToArray());
        References = Array.AsReadOnly(references.ToArray());
        Limits = limits;
    }

    public IReadOnlyList<RowAddress> PlannedRows { get; }
    public IReadOnlyList<RowReference> References { get; }
    public RowGraphLimits Limits { get; }
}

public sealed class RowGraphAnalysis
{
    public RowGraphAnalysis(IEnumerable<RowAddress> insertionOrder, IEnumerable<RowAddress> unreachedRows, IEnumerable<MissingReference> missingReferences)
    {
        InsertionOrder = Array.AsReadOnly(insertionOrder.ToArray());
        UnreachedRows = Array.AsReadOnly(unreachedRows.ToArray());
        MissingReferences = Array.AsReadOnly(missingReferences.ToArray());
    }

    public IReadOnlyList<RowAddress> InsertionOrder { get; }
    public IReadOnlyList<RowAddress> UnreachedRows { get; }
    public IReadOnlyList<MissingReference> MissingReferences { get; }
    public bool IsAcyclic => UnreachedRows.Count == 0;
}

public interface ISetBasedRowGraphAnalyzer
{
    Task<RowGraphAnalysis> AnalyzeAsync(RowGraphRequest request, IReadOnlyCollection<ForeignKeyDefinition> excludedRelationships, CancellationToken cancellationToken);
}
```

The analyzer contract requires a provider implementation to classify every non-NULL reference as `Planned`, `TargetSatisfied`, or `Missing` while staging keys. It must turn child-to-parent dependencies into parent-before-child frontier operations. The Core interface has no row iteration, provider library, database type, or database command; it carries the explicit limits that later PostgreSQL and SQL Server implementations must enforce.

- [ ] **Step 4: Run the focused lane and confirm the models and boundary pass.**

Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~RowGraphModelsTests|FullyQualifiedName~Core_HasNoProjectOrPackageDependencies"`

Expected: exit 0; both row-graph model facts and `Core_HasNoProjectOrPackageDependencies` pass, and the unit lane prints informational coverage without enforcing a threshold.

- [ ] **Step 5: Commit the contracts and architecture assertion.**

Run: `git add src/DataPitcher.Core/Graph/RowGraph.cs tests/DataPitcher.UnitTests/Graph/RowGraphModelsTests.cs tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs && git commit -m "feat: define row graph analysis contracts"`

### Task 2: Select one cycle-breaking edge-set strategy

**Files:**
- Create: `src/DataPitcher.Core/Graph/CycleStrategySelector.cs`, `tests/DataPitcher.UnitTests/Graph/CycleStrategySelectorTests.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Graph/CycleStrategySelectorTests.cs`

- [ ] **Step 1: Write the failing outcome-selection tests.**

`tests/DataPitcher.UnitTests/Graph/CycleStrategySelectorTests.cs`:

```csharp
using DataPitcher.Core.Closure;
using DataPitcher.Core.Graph;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Graph;

public sealed class CycleStrategySelectorTests
{
    [Fact]
    public async Task SelectAsync_WhenSelfReferencingRowsAreAForest_ReturnsOrderedBeforeAnyCapability()
    {
        var employees = T("Employees"); var manager = F("FK_Manager", employees, employees);
        var root = R(employees, 1); var child = R(employees, 2); var request = Request([root, child], [new(root, manager, null, RowReferenceState.NullParent), new(child, manager, root, RowReferenceState.Planned)]);
        var analyzer = new ScriptedAnalyzer(("", Ordered(root, child)));
        var result = await new CycleStrategySelector(analyzer).SelectAsync(request, Capabilities(false), CancellationToken.None);
        Assert.Equal(CycleStrategy.Ordered, result.Strategy); Assert.Empty(result.CycleBreakingEdges); Assert.Equal([""], analyzer.Calls);
    }

    [Fact]
    public async Task SelectAsync_WhenSelfReferencingRowsFormAGenuineCycle_UsesPostgreSqlDeferredEdges()
    {
        var employees = T("Employees"); var manager = F("FK_Manager", employees, employees); var a = R(employees, 1); var b = R(employees, 2);
        var analyzer = new ScriptedAnalyzer(("", Cyclic(a, b)), ("FK_Manager", Ordered(a, b)));
        var result = await new CycleStrategySelector(analyzer).SelectAsync(Request([a, b], [new(a, manager, b, RowReferenceState.Planned), new(b, manager, a, RowReferenceState.Planned)]), Capabilities(true, new(manager, true, false, false)), CancellationToken.None);
        Assert.Equal(CycleStrategy.Deferred, result.Strategy); Assert.Equal([manager], result.CycleBreakingEdges); Assert.Equal(["", "FK_Manager"], analyzer.Calls);
    }

    [Fact]
    public async Task SelectAsync_WhenTwoTablesFormACycle_UsesOneSuspendableEdgeSet()
    {
        var aTable = T("A"); var bTable = T("B"); var ab = F("FK_A_B", aTable, bTable); var ba = F("FK_B_A", bTable, aTable); var a = R(aTable, 1); var b = R(bTable, 1);
        var analyzer = new ScriptedAnalyzer(("", Cyclic(a, b)), ("FK_A_B", Ordered(b, a)));
        var result = await new CycleStrategySelector(analyzer).SelectAsync(Request([a, b], [new(a, ab, b, RowReferenceState.Planned), new(b, ba, a, RowReferenceState.Planned)]), Capabilities(false, new(ab, true, false, true)), CancellationToken.None);
        Assert.Equal(CycleStrategy.ConstraintSuspension, result.Strategy); Assert.Equal([ab], result.CycleBreakingEdges);
    }

    [Fact]
    public async Task SelectAsync_WhenNullableEdgesBreakEveryCycle_UsesNullableTwoPhase()
    {
        var aTable = T("A"); var bTable = T("B"); var ab = F("FK_A_B", aTable, bTable); var ba = F("FK_B_A", bTable, aTable); var a = R(aTable, 1); var b = R(bTable, 1);
        var analyzer = new ScriptedAnalyzer(("", Cyclic(a, b)), ("FK_A_B", Ordered(b, a)));
        var result = await new CycleStrategySelector(analyzer).SelectAsync(Request([a, b], [new(a, ab, b, RowReferenceState.Planned), new(b, ba, a, RowReferenceState.Planned)]), Capabilities(false, new(ab, false, true, false)), CancellationToken.None);
        Assert.Equal(CycleStrategy.NullableTwoPhase, result.Strategy); Assert.Equal([ab], result.CycleBreakingEdges);
    }

    [Fact]
    public async Task SelectAsync_WhenNullableEdgesLeaveAnotherCycle_DoesNotCombineStrategiesAndBlocks()
    {
        var aTable = T("A"); var bTable = T("B"); var ab = F("FK_A_B", aTable, bTable); var ba = F("FK_B_A", bTable, aTable); var bb = F("FK_B_B", bTable, bTable); var a = R(aTable, 1); var b = R(bTable, 1);
        var analyzer = new ScriptedAnalyzer(("", Cyclic(a, b)), ("FK_A_B", Cyclic(a, b)), ("FK_B_B", Cyclic(a, b)));
        var result = await new CycleStrategySelector(analyzer).SelectAsync(Request([a, b], [new(a, ab, b, RowReferenceState.Planned), new(b, ba, a, RowReferenceState.Planned), new(b, bb, b, RowReferenceState.Planned)]), Capabilities(false, new(ab, false, true, false), new(bb, false, false, true)), CancellationToken.None);
        Assert.Equal(CycleStrategy.Blocked, result.Strategy); Assert.True(result.MustBlockScc); Assert.Equal(["", "FK_A_B", "FK_B_B"], analyzer.Calls);
    }

    [Fact]
    public async Task SelectAsync_WhenNoEligibleEdgeSetBreaksTheCycle_BlocksWithTablesAndRelationships()
    {
        var alpha = T("Alpha"); var beta = T("Beta"); var ab = F("FK_Alpha_Beta", alpha, beta); var ba = F("FK_Beta_Alpha", beta, alpha); var a = R(alpha, 1); var b = R(beta, 1);
        var result = await new CycleStrategySelector(new ScriptedAnalyzer(("", Cyclic(a, b)))).SelectAsync(Request([a, b], [new(a, ab, b, RowReferenceState.Planned), new(b, ba, a, RowReferenceState.Planned)]), Capabilities(false), CancellationToken.None);
        Assert.Equal(CycleStrategy.Blocked, result.Strategy); Assert.Contains("dbo.Alpha", result.Explanation); Assert.Contains("dbo.Beta", result.Explanation); Assert.Contains("FK_Alpha_Beta", result.Explanation); Assert.Contains("FK_Beta_Alpha", result.Explanation);
    }

    private static TableDefinition T(string name) => new("dbo", name, [], new($"PK_{name}", ["Id"]), []);
    private static ForeignKeyDefinition F(string name, TableDefinition child, TableDefinition parent) => new(name, child, parent, ["RefId"], ["Id"], true, true);
    private static RowAddress R(TableDefinition table, int id) => new(table, new StableKey([new("Id", id)]));
    private static RowGraphRequest Request(RowAddress[] rows, RowReference[] references) => new(rows, references, new(10, TimeSpan.FromSeconds(5), 1_000_000));
    private static RowGraphAnalysis Ordered(params RowAddress[] rows) => new(rows, [], []);
    private static RowGraphAnalysis Cyclic(params RowAddress[] rows) => new([], rows, []);
    private static CycleStrategyCapabilities Capabilities(bool supportsDeferred, params CycleEdgeCapability[] edges) => new(supportsDeferred, edges);

    private sealed class ScriptedAnalyzer(params (string Excluded, RowGraphAnalysis Analysis)[] answers) : ISetBasedRowGraphAnalyzer
    {
        private readonly Dictionary<string, RowGraphAnalysis> _answers = answers.ToDictionary(x => x.Excluded, x => x.Analysis);
        public List<string> Calls { get; } = [];
        public Task<RowGraphAnalysis> AnalyzeAsync(RowGraphRequest request, IReadOnlyCollection<ForeignKeyDefinition> excludedRelationships, CancellationToken cancellationToken)
        {
            var key = string.Join(",", excludedRelationships.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
            Calls.Add(key); return Task.FromResult(_answers[key]);
        }
    }
}
```

The scripted analyzer intentionally returns only precomputed provider results. It proves selector call ordering and edge-set choice without smuggling an application-side row loop into production or tests. The future provider contract tests must independently prove recursive-CTE and multi-edge frontier execution against staged data.

- [ ] **Step 2: Run the focused lane and confirm the selector is absent.**

Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~CycleStrategySelectorTests"`

Expected: compilation fails with CS0246 stating that `CycleStrategySelector`, `CycleStrategy`, `CycleStrategyCapabilities`, and `CycleEdgeCapability` could not be found.

- [ ] **Step 3: Implement the five-outcome selector without strategy mixing.**

`src/DataPitcher.Core/Graph/CycleStrategySelector.cs`:

```csharp
using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Graph;

public enum CycleStrategy { Ordered, Deferred, NullableTwoPhase, ConstraintSuspension, Blocked }

public sealed record CycleEdgeCapability(ForeignKeyDefinition Relationship, bool CanDeferCycleBreakingFk, bool CanUseNullableFkTwoPhase, bool CanSafelySuspendFk);

public sealed class CycleStrategyCapabilities
{
    public CycleStrategyCapabilities(bool supportsDeferrableForeignKeys, IEnumerable<CycleEdgeCapability> edgeCapabilities)
    {
        SupportsDeferrableForeignKeys = supportsDeferrableForeignKeys;
        EdgeCapabilities = Array.AsReadOnly(edgeCapabilities.ToArray());
    }

    public bool SupportsDeferrableForeignKeys { get; }
    public IReadOnlyList<CycleEdgeCapability> EdgeCapabilities { get; }
    public bool CanDeferCycleBreakingFks => SupportsDeferrableForeignKeys && EdgeCapabilities.Any(x => x.CanDeferCycleBreakingFk);
    public bool CanUseNullableFkTwoPhase => EdgeCapabilities.Any(x => x.CanUseNullableFkTwoPhase);
    public bool CanSafelySuspendFks => EdgeCapabilities.Any(x => x.CanSafelySuspendFk);
}

public sealed class CycleStrategySelection
{
    public CycleStrategySelection(CycleStrategy strategy, IEnumerable<ForeignKeyDefinition> cycleBreakingEdges, RowGraphAnalysis analysis, string explanation)
    {
        Strategy = strategy;
        CycleBreakingEdges = Array.AsReadOnly(cycleBreakingEdges.ToArray());
        Analysis = analysis;
        Explanation = explanation;
    }

    public CycleStrategy Strategy { get; }
    public IReadOnlyList<ForeignKeyDefinition> CycleBreakingEdges { get; }
    public RowGraphAnalysis Analysis { get; }
    public string Explanation { get; }
    public bool MustBlockScc => Strategy == CycleStrategy.Blocked;
}

public sealed class CycleStrategySelector(ISetBasedRowGraphAnalyzer analyzer)
{
    public async Task<CycleStrategySelection> SelectAsync(RowGraphRequest request, CycleStrategyCapabilities capabilities, CancellationToken cancellationToken)
    {
        var initial = await analyzer.AnalyzeAsync(request, [], cancellationToken);
        if (initial.IsAcyclic)
            return new(CycleStrategy.Ordered, [], initial, "The planned row graph is acyclic.");

        foreach (var candidate in Candidates(capabilities))
        {
            var residual = await analyzer.AnalyzeAsync(request, candidate.Edges, cancellationToken);
            if (residual.IsAcyclic)
                return new(candidate.Strategy, candidate.Edges, residual, $"{candidate.Strategy} breaks every planned row cycle.");
        }

        return new(CycleStrategy.Blocked, [], initial, BlockedExplanation(request));
    }

    private static IEnumerable<(CycleStrategy Strategy, IReadOnlyList<ForeignKeyDefinition> Edges)> Candidates(CycleStrategyCapabilities capabilities)
    {
        if (capabilities.CanDeferCycleBreakingFks)
            yield return (CycleStrategy.Deferred, Eligible(capabilities, x => x.CanDeferCycleBreakingFk));
        if (capabilities.CanUseNullableFkTwoPhase)
            yield return (CycleStrategy.NullableTwoPhase, Eligible(capabilities, x => x.CanUseNullableFkTwoPhase));
        if (capabilities.CanSafelySuspendFks)
            yield return (CycleStrategy.ConstraintSuspension, Eligible(capabilities, x => x.CanSafelySuspendFk));
    }

    private static IReadOnlyList<ForeignKeyDefinition> Eligible(CycleStrategyCapabilities capabilities, Func<CycleEdgeCapability, bool> eligible) =>
        Array.AsReadOnly(capabilities.EdgeCapabilities.Where(eligible).Select(x => x.Relationship).Distinct().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray());

    private static string BlockedExplanation(RowGraphRequest request)
    {
        var tables = request.PlannedRows.Select(x => x.Table).Distinct().OrderBy(x => $"{x.Schema}.{x.Name}", StringComparer.Ordinal).Select(x => $"{x.Schema}.{x.Name}");
        var relationships = request.References.Select(x => x.Relationship).Distinct().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name);
        return $"No single eligible cycle-breaking edge set orders the component. Affected tables: {string.Join(", ", tables)}. Relationships: {string.Join(", ", relationships)}.";
    }
}
```

The three provider-supplied capability dimensions remain per foreign-key edge so eligibility can differ within one SCC. A PostgreSQL adapter sets deferred capability only after its catalog probe confirms declared deferrability and required enforcement/validation; SQL Server never does. The nullable and suspension bits are similarly affirmative safety facts, not hints. `Blocked` remains a selector-derived result after every complete single-strategy edge set leaves unreached rows.

- [ ] **Step 4: Run the selector suite in the Docker-free lane and confirm all outcomes pass.**

Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~CycleStrategySelectorTests"`

Expected: exit 0; the six facts pass, covering an acyclic self-reference forest, a genuine self-cycle, a two-table component, nullable success, nullable residual-cycle rejection without mixed strategies, and a clearly named blocked component. The architecture tests also pass in the lane.

- [ ] **Step 5: Commit the selector and its behavioural fixtures.**

Run: `git add src/DataPitcher.Core/Graph/CycleStrategySelector.cs tests/DataPitcher.UnitTests/Graph/CycleStrategySelectorTests.cs && git commit -m "feat: select row cycle strategies by edge set"`

## Self-Review

This plan covers the table-SCC versus row-cycle distinction; ordered-first selection; row-reference roots for NULL and target-satisfied parents; separate missing-reference reporting; a provider-side set-based detection contract with O(V+E) complexity and explicit recursion, timeout, and temporary-space bounds; PostgreSQL-only deferred eligibility; nullable and suspension capability facts; complete per-edge residual testing; no cross-strategy mixing; and a `Blocked` explanation naming tables and relationships. Its tests cover the required forest, genuine row cycle, two-table cycle, both mixed-nullability outcomes, and no-eligible-strategy result. Core's no-dependency architecture assertion, warnings-as-errors inheritance, Docker-free `scripts/test-unit.sh` lane, and merged-only `scripts/test-all.sh` coverage rule are stated and preserved.

Deferred work is exactly provider staging and SQL/CTE frontier execution, catalog capability probing, database strategy execution, transactions, nullable update passes, suspension/recovery journals, revalidation, and Docker integration tests. `MissingReference` is intentionally handed to the higher plan validator rather than becoming a sixth strategy. Type and method names were checked across tasks: Task 2 uses Task 1's `RowGraphRequest`, `RowGraphAnalysis`, `ISetBasedRowGraphAnalyzer`, `RowReference`, `RowReferenceState`, and `MissingReference` exactly as defined; its test helper signatures match the selector constructors and `SelectAsync` method.
