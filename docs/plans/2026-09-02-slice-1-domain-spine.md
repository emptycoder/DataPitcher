# DataPitcher Slice 1: Domain Spine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish a provider-free, fully unit-tested domain spine that computes minimal dependency closure sets.

**Architecture:** Core owns identity, schema, graph, and closure semantics and references no framework or provider. `IClosureStore` isolates the breadth-first closure from persistence, so an in-memory fake can prove the algorithm without a database. Later provider projects implement the same seam with set-based database operations.

**Tech Stack:** .NET SDK 10.0.400, C# latest, xUnit, Coverlet collector, XML project inspection, Bash, PowerShell.

---

## File Structure

- `DataPitcher.sln` — solution containing the Core and test projects.
- `Directory.Build.props` — repository compiler defaults.
- `global.json` — pinned SDK.
- `src/DataPitcher.Core/DataPitcher.Core.csproj` — dependency-free domain assembly.
- `src/DataPitcher.Core/Identity/StableKey.cs` — ordered, comparable row identity.
- `src/DataPitcher.Core/Schema/ColumnDefinition.cs` — column metadata.
- `src/DataPitcher.Core/Schema/UniqueConstraint.cs` — candidate stable-key metadata.
- `src/DataPitcher.Core/Schema/TableDefinition.cs` — table metadata.
- `src/DataPitcher.Core/Schema/ForeignKeyDefinition.cs` — directed relationship metadata.
- `src/DataPitcher.Core/Schema/StableKeySelector.cs` — stable-key choice and blocked result.
- `src/DataPitcher.Core/Graph/DependencyGraph.cs` — dual-indexed foreign-key graph.
- `src/DataPitcher.Core/Graph/TarjanScc.cs` — strongly connected components.
- `src/DataPitcher.Core/Graph/CondensedGraph.cs` — DAG and topological layers.
- `src/DataPitcher.Core/Closure/IClosureStore.cs` — four-operation persistence seam.
- `src/DataPitcher.Core/Closure/ClosureModels.cs` — closure requests, rows, and policies.
- `src/DataPitcher.Core/Closure/DependencyClosure.cs` — target-aware generation loop.
- `tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj` — Core unit-test assembly.
- `tests/DataPitcher.UnitTests/Identity/StableKeyTests.cs` — key equality and ordering tests.
- `tests/DataPitcher.UnitTests/Schema/ForeignKeyDefinitionTests.cs` — FK invariant test.
- `tests/DataPitcher.UnitTests/Schema/StableKeySelectorTests.cs` — key-selection tests.
- `tests/DataPitcher.UnitTests/Graph/DependencyGraphTests.cs` — graph-edge tests.
- `tests/DataPitcher.UnitTests/Graph/TarjanSccTests.cs` — SCC and condensation tests.
- `tests/DataPitcher.UnitTests/Closure/InMemoryClosureStore.cs` — deterministic store fake.
- `tests/DataPitcher.UnitTests/Closure/DependencyClosureTests.cs` — closure behavior fixtures.
- `tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj` — project-boundary tests.
- `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs` — reference-rule assertions.
- `scripts/test-all.sh` — Unix build, tests, and 100% coverage gate.
- `scripts/test-all.ps1` — PowerShell equivalent.
- `docs/plans/2026-09-02-slice-1-domain-spine.md` — this implementation plan.

## Docker Blocker

Slice 1 excludes Testcontainers, Docker Compose, Playwright, provider integration, database-backed closure storage, and any network or database-server verification. Docker is unavailable, so those checks cannot run honestly on this machine.

Task 6's `IClosureStore` is the seam: its database-backed implementation belongs to a later Docker-blocked slice, while the Core algorithm runs against the in-memory fake now. That makes the excluded work droppable in later without changing the algorithm.

### Task 1: Solution and project scaffold

**Files:**
- Create: `DataPitcher.sln`, `Directory.Build.props`, `global.json`, `src/DataPitcher.Core/DataPitcher.Core.csproj`, `tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj`
- Modify: none
- Test: none; the verification intentionally establishes an empty test inventory.

- [ ] **Step 1: Create the empty solution and project files**

```xml
<!-- Directory.Build.props -->
<Project><PropertyGroup><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><LangVersion>latest</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>
<!-- src/DataPitcher.Core/DataPitcher.Core.csproj -->
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>DataPitcher</RootNamespace></PropertyGroup></Project>
<!-- tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj -->
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><ProjectReference Include="../../src/DataPitcher.Core/DataPitcher.Core.csproj" /><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" /><PackageReference Include="xunit" Version="2.9.3" /><PackageReference Include="xunit.runner.visualstudio" Version="3.1.4"><PrivateAssets>all</PrivateAssets></PackageReference><PackageReference Include="coverlet.collector" Version="6.0.4"><PrivateAssets>all</PrivateAssets></PackageReference></ItemGroup></Project>
```

```json
{ "sdk": { "version": "10.0.400", "rollForward": "latestPatch" } }
```

Run `dotnet new sln --name DataPitcher`, then `dotnet sln DataPitcher.sln add src/DataPitcher.Core/DataPitcher.Core.csproj tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj`.

- [ ] **Step 2: Build the scaffold and confirm the empty test run**

Run: `dotnet build && dotnet test`

Expected: build succeeds; test output reports `Total tests: 0. Passed: 0. Failed: 0.`

- [ ] **Step 3: Commit**

Run: `git add DataPitcher.sln Directory.Build.props global.json src/DataPitcher.Core/DataPitcher.Core.csproj tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj && git commit -m "chore: scaffold domain solution"`

### Task 2: StableKey value type

**Files:**
- Create: `src/DataPitcher.Core/Identity/StableKey.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Identity/StableKeyTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using DataPitcher.Core.Identity;
using Xunit;

namespace DataPitcher.UnitTests.Identity;

public sealed class StableKeyTests
{
    [Fact] public void StableKey_WhenSameColumnsAndValues_AreEqual()
    {
        var left = new StableKey([new("A", 1), new("B", "x")]);
        var right = new StableKey([new("A", 1), new("B", "x")]);
        Assert.Equal(left, right);
    }

    [Fact] public void StableKey_WhenColumnOrderDiffers_AreNotEqual()
    {
        var left = new StableKey([new("A", 1), new("B", 2)]);
        var right = new StableKey([new("B", 2), new("A", 1)]);
        Assert.NotEqual(left, right);
    }

    [Fact] public void StableKey_WhenValueIsNull_ComparesConsistently()
    {
        var nullKey = new StableKey([new("A", null)]);
        var valueKey = new StableKey([new("A", 1)]);
        Assert.Equal(0, nullKey.CompareTo(new StableKey([new("A", null)])));
        Assert.True(nullKey.CompareTo(valueKey) < 0);
    }

    [Fact] public void StableKey_WhenSorted_ProducesDeterministicTotalOrder()
    {
        var keys = new List<StableKey> { new([new("A", 2)]), new([new("B", 0)]), new([new("A", null)]), new([new("A", 1)]) };
        keys.Sort();
        Assert.Equal([new StableKey([new("A", null)]), new StableKey([new("A", 1)]), new StableKey([new("A", 2)]), new StableKey([new("B", 0)])], keys);
    }
}
```

- [ ] **Step 2: Run them and confirm the right failure**

Run: `dotnet test --filter "FullyQualifiedName~StableKeyTests"`

Expected: compilation fails with `The type or namespace name 'Identity' does not exist in the namespace 'DataPitcher.Core'`.

- [ ] **Step 3: Write the minimal implementation**

```csharp
using System.Globalization;

namespace DataPitcher.Core.Identity;

public readonly record struct KeyComponent(string Column, IComparable? Value);

public sealed class StableKey : IEquatable<StableKey>, IComparable<StableKey>
{
    private readonly KeyComponent[] _components;
    public StableKey(IEnumerable<KeyComponent> components) => _components = components.ToArray();
    public IReadOnlyList<KeyComponent> Components => _components;
    public bool Equals(StableKey? other) => other is not null && _components.AsSpan().SequenceEqual(other._components);
    public override bool Equals(object? obj) => obj is StableKey other && Equals(other);
    public override int GetHashCode() { var hash = new HashCode(); foreach (var component in _components) hash.Add(component); return hash.ToHashCode(); }
    public int CompareTo(StableKey? other)
    {
        if (other is null) return 1;
        foreach (var pair in _components.Zip(other._components))
        {
            var column = StringComparer.Ordinal.Compare(pair.First.Column, pair.Second.Column);
            if (column != 0) return column;
            var value = CompareValues(pair.First.Value, pair.Second.Value);
            if (value != 0) return value;
        }
        return _components.Length.CompareTo(other._components.Length);
    }
    private static int CompareValues(IComparable? left, IComparable? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        var type = StringComparer.Ordinal.Compare(left.GetType().AssemblyQualifiedName, right.GetType().AssemblyQualifiedName);
        if (type != 0) return type;
        var result = left.CompareTo(right);
        return result != 0 ? result : StringComparer.Ordinal.Compare(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture));
    }
}
```

- [ ] **Step 4: Run them and confirm they pass**

Run: `dotnet test --filter "FullyQualifiedName~StableKeyTests"`

Expected: `Passed: 4. Failed: 0.`

- [ ] **Step 5: Commit**

Run: `git add src/DataPitcher.Core/Identity/StableKey.cs tests/DataPitcher.UnitTests/Identity/StableKeyTests.cs && git commit -m "feat: add stable key value type"`

### Task 3: Schema model and stable-key selection

**Files:**
- Create: `src/DataPitcher.Core/Schema/ColumnDefinition.cs`, `src/DataPitcher.Core/Schema/UniqueConstraint.cs`, `src/DataPitcher.Core/Schema/TableDefinition.cs`, `src/DataPitcher.Core/Schema/ForeignKeyDefinition.cs`, `src/DataPitcher.Core/Schema/StableKeySelector.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Schema/ForeignKeyDefinitionTests.cs`, `tests/DataPitcher.UnitTests/Schema/StableKeySelectorTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Schema;

public sealed class ForeignKeyDefinitionTests
{
    [Fact] public void ForeignKey_WhenChildAndParentColumnCountsDiffer_IsRejected()
    {
        var child = Table("Child"); var parent = Table("Parent");
        Assert.Throws<ArgumentException>(() => new ForeignKeyDefinition("FK", child, parent, ["A"], ["A", "B"], true, true));
    }
    private static TableDefinition Table(string name) => new("dbo", name, [], null, []);
}

public sealed class StableKeySelectorTests
{
    [Fact] public void StableKeySelector_WhenTableHasPrimaryKey_UsesPrimaryKey()
    {
        var primary = new UniqueConstraint("PK_Orders", ["Id"]);
        Assert.Equal(primary, StableKeySelector.Select(new TableDefinition("dbo", "Orders", [], primary, []), null).Constraint);
    }
    [Fact] public void StableKeySelector_WhenNoPrimaryKeyAndNoSelectedUnique_ReportsBlocked()
    {
        var result = StableKeySelector.Select(new TableDefinition("dbo", "Logs", [], null, [new("UQ_Code", ["Code"])]), null);
        Assert.False(result.HasStableKey);
    }
    [Fact] public void StableKeySelector_WhenUniqueConstraintExplicitlySelected_UsesIt()
    {
        var unique = new UniqueConstraint("UQ_Code", ["Code"]);
        Assert.Equal(unique, StableKeySelector.Select(new TableDefinition("dbo", "Codes", [], null, [unique]), "UQ_Code").Constraint);
    }
}
```

- [ ] **Step 2: Run them and confirm the right failure**

Run: `dotnet test --filter "FullyQualifiedName~ForeignKeyDefinitionTests|FullyQualifiedName~StableKeySelectorTests"`

Expected: compilation fails with `The type or namespace name 'Schema' does not exist in the namespace 'DataPitcher.Core'`.

- [ ] **Step 3: Write the minimal schema model**

```csharp
namespace DataPitcher.Core.Schema;
public sealed record ColumnDefinition(string Name, Type ClrType, bool IsNullable);
public sealed record UniqueConstraint(string Name, IReadOnlyList<string> Columns);
public sealed record TableDefinition(string Schema, string Name, IReadOnlyList<ColumnDefinition> Columns, UniqueConstraint? PrimaryKey, IReadOnlyList<UniqueConstraint> UniqueConstraints);
public sealed class ForeignKeyDefinition
{
    public ForeignKeyDefinition(string name, TableDefinition childTable, TableDefinition parentTable, IReadOnlyList<string> childColumns, IReadOnlyList<string> parentColumns, bool isEnforced, bool isTrusted)
    {
        if (childColumns.Count != parentColumns.Count) throw new ArgumentException("Foreign-key child and parent column counts must match.");
        Name = name; ChildTable = childTable; ParentTable = parentTable; ChildColumns = childColumns; ParentColumns = parentColumns; IsEnforced = isEnforced; IsTrusted = isTrusted;
    }
    public string Name { get; } public TableDefinition ChildTable { get; } public TableDefinition ParentTable { get; }
    public IReadOnlyList<string> ChildColumns { get; } public IReadOnlyList<string> ParentColumns { get; }
    public bool IsEnforced { get; } public bool IsTrusted { get; }
}
public sealed record StableKeySelection(UniqueConstraint? Constraint)
{
    public bool HasStableKey => Constraint is not null;
    public static StableKeySelection NoStableKey { get; } = new(null);
}
public static class StableKeySelector
{
    public static StableKeySelection Select(TableDefinition table, string? selectedUniqueConstraint) => table.PrimaryKey is not null
        ? new(table.PrimaryKey)
        : table.UniqueConstraints.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.Name, selectedUniqueConstraint)) is { } unique ? new(unique) : StableKeySelection.NoStableKey;
}
```

- [ ] **Step 4: Run them and confirm they pass**

Run: `dotnet test --filter "FullyQualifiedName~ForeignKeyDefinitionTests|FullyQualifiedName~StableKeySelectorTests"`

Expected: `Passed: 4. Failed: 0.`

- [ ] **Step 5: Commit**

Run: `git add src/DataPitcher.Core/Schema tests/DataPitcher.UnitTests/Schema && git commit -m "feat: model schema identities and relationships"`

### Task 4: Dependency graph

**Files:**
- Create: `src/DataPitcher.Core/Graph/DependencyGraph.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Graph/DependencyGraphTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using DataPitcher.Core.Graph; using DataPitcher.Core.Schema; using Xunit;
namespace DataPitcher.UnitTests.Graph;
public sealed class DependencyGraphTests
{
    [Fact] public void DependencyGraph_WhenOrderReferencesCustomer_EdgeRunsFromOrderToCustomer()
    { var orders = T("Orders"); var customers = T("Customers"); var fk = F("Customer", orders, customers); var graph = new DependencyGraph([orders, customers], [fk]); Assert.Equal(fk, Assert.Single(graph.DependenciesOf(orders))); Assert.Equal(fk, Assert.Single(graph.DependentsOf(customers))); }
    [Fact] public void DependencyGraph_WhenTwoForeignKeysBetweenSameTables_KeepsBothEdgesDistinct()
    { var orders = T("Orders"); var people = T("People"); var graph = new DependencyGraph([orders, people], [F("BillTo", orders, people), F("ShipTo", orders, people)]); Assert.Equal(2, graph.DependenciesOf(orders).Count); }
    [Fact] public void DependencyGraph_WhenForeignKeyIsSelfReferencing_ProducesSelfEdge()
    { var employees = T("Employees"); var fk = F("Manager", employees, employees); var graph = new DependencyGraph([employees], [fk]); Assert.Equal(fk, Assert.Single(graph.DependenciesOf(employees))); }
    private static TableDefinition T(string name) => new("dbo", name, [], null, []);
    private static ForeignKeyDefinition F(string name, TableDefinition child, TableDefinition parent) => new(name, child, parent, ["Id"], ["Id"], true, true);
}
```

- [ ] **Step 2: Run them and confirm the right failure**

Run: `dotnet test --filter "FullyQualifiedName~DependencyGraphTests"`

Expected: compilation fails with `The type or namespace name 'Graph' does not exist in the namespace 'DataPitcher.Core'`.

- [ ] **Step 3: Write the minimal implementation**

```csharp
using DataPitcher.Core.Schema;
namespace DataPitcher.Core.Graph;
public sealed class DependencyGraph
{
    private readonly Dictionary<TableDefinition, List<ForeignKeyDefinition>> _dependencies;
    private readonly Dictionary<TableDefinition, List<ForeignKeyDefinition>> _dependents;
    public DependencyGraph(IEnumerable<TableDefinition> tables, IEnumerable<ForeignKeyDefinition> foreignKeys)
    {
        Tables = tables.Distinct().ToArray(); _dependencies = Tables.ToDictionary(x => x, _ => new()); _dependents = Tables.ToDictionary(x => x, _ => new());
        foreach (var foreignKey in foreignKeys) { _dependencies[foreignKey.ChildTable].Add(foreignKey); _dependents[foreignKey.ParentTable].Add(foreignKey); }
    }
    public IReadOnlyList<TableDefinition> Tables { get; }
    public IReadOnlyList<ForeignKeyDefinition> DependenciesOf(TableDefinition table) => _dependencies[table];
    public IReadOnlyList<ForeignKeyDefinition> DependentsOf(TableDefinition table) => _dependents[table];
}
```

- [ ] **Step 4: Run them and confirm they pass**

Run: `dotnet test --filter "FullyQualifiedName~DependencyGraphTests"`

Expected: `Passed: 3. Failed: 0.`

- [ ] **Step 5: Commit**

Run: `git add src/DataPitcher.Core/Graph/DependencyGraph.cs tests/DataPitcher.UnitTests/Graph/DependencyGraphTests.cs && git commit -m "feat: add dependency graph"`

### Task 5: Strongly connected components and condensation

**Files:**
- Create: `src/DataPitcher.Core/Graph/TarjanScc.cs`, `src/DataPitcher.Core/Graph/CondensedGraph.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Graph/TarjanSccTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using DataPitcher.Core.Graph; using DataPitcher.Core.Schema; using Xunit;
namespace DataPitcher.UnitTests.Graph;
public sealed class TarjanSccTests
{
    [Fact] public void TarjanScc_WhenGraphIsAcyclic_EachNodeIsItsOwnComponent() { var (g, a, b, _) = G(("A", "B"), ("B", "C")); Assert.All(TarjanScc.Find(g), c => Assert.Single(c.Tables)); }
    [Fact] public void TarjanScc_WhenTwoTablesFormCycle_ProducesSingleComponentOfSizeTwo() { var (g, a, b, _) = G(("A", "B"), ("B", "A")); Assert.Contains(TarjanScc.Find(g), c => c.Tables.Count == 2 && c.Tables.Contains(a) && c.Tables.Contains(b)); }
    [Fact] public void TarjanScc_WhenNodeHasOnlySelfEdge_DoesNotCreateMultiNodeComponent() { var (g, _, _, _) = G(("A", "A")); Assert.Single(Assert.Single(TarjanScc.Find(g)).Tables); }
    [Fact] public void CondensedGraph_IsAlwaysAcyclic() { var (g, _, _, _) = G(("A", "B"), ("B", "A"), ("B", "C")); Assert.True(new CondensedGraph(g).IsAcyclic()); }
    [Fact] public void CondensedGraph_TopologicalLayers_RespectEveryEdge() { var (g, _, _, _) = G(("A", "B"), ("B", "C")); var condensed = new CondensedGraph(g); var layer = condensed.TopologicalLayers().SelectMany((x, i) => x.Select(id => (id, i))).ToDictionary(x => x.id, x => x.i); Assert.All(condensed.Edges, edge => Assert.True(layer[edge.From] < layer[edge.To])); }
    private static (DependencyGraph Graph, TableDefinition A, TableDefinition B, TableDefinition C) G(params (string Child, string Parent)[] edges) { var names = edges.SelectMany(x => new[] { x.Child, x.Parent }).Distinct().ToArray(); var tables = names.ToDictionary(x => x, x => new TableDefinition("dbo", x, [], null, [])); return (new DependencyGraph(tables.Values, edges.Select((x, i) => new ForeignKeyDefinition($"FK{i}", tables[x.Child], tables[x.Parent], ["Id"], ["Id"], true, true))), tables.GetValueOrDefault("A")!, tables.GetValueOrDefault("B")!, tables.GetValueOrDefault("C")!); }
}
```

- [ ] **Step 2: Run them and confirm the right failure**

Run: `dotnet test --filter "FullyQualifiedName~TarjanSccTests"`

Expected: compilation fails with `The name 'TarjanScc' does not exist in the current context`.

- [ ] **Step 3: Write the minimal implementations**

```csharp
// src/DataPitcher.Core/Graph/TarjanScc.cs
using DataPitcher.Core.Schema;
namespace DataPitcher.Core.Graph;
public sealed record Scc(int Id, IReadOnlyList<TableDefinition> Tables);
public static class TarjanScc
{
    public static IReadOnlyList<Scc> Find(DependencyGraph graph)
    {
        var index = 0; var stack = new Stack<TableDefinition>(); var active = new HashSet<TableDefinition>(); var indices = new Dictionary<TableDefinition, int>(); var low = new Dictionary<TableDefinition, int>(); var result = new List<Scc>();
        void Visit(TableDefinition table) { indices[table] = low[table] = index++; stack.Push(table); active.Add(table); foreach (var edge in graph.DependenciesOf(table)) { var parent = edge.ParentTable; if (!indices.ContainsKey(parent)) { Visit(parent); low[table] = Math.Min(low[table], low[parent]); } else if (active.Contains(parent)) low[table] = Math.Min(low[table], indices[parent]); } if (low[table] != indices[table]) return; var members = new List<TableDefinition>(); TableDefinition member; do { member = stack.Pop(); active.Remove(member); members.Add(member); } while (!ReferenceEquals(member, table)); result.Add(new Scc(result.Count, members)); }
        foreach (var table in graph.Tables) if (!indices.ContainsKey(table)) Visit(table); return result;
    }
}
```

```csharp
// src/DataPitcher.Core/Graph/CondensedGraph.cs
namespace DataPitcher.Core.Graph;
public sealed record CondensedEdge(int From, int To);
public sealed class CondensedGraph
{
    public CondensedGraph(DependencyGraph graph) { Components = TarjanScc.Find(graph); var owner = Components.SelectMany(c => c.Tables.Select(t => (t, c.Id))).ToDictionary(x => x.t, x => x.Id); Edges = graph.Tables.SelectMany(graph.DependenciesOf).Select(f => new CondensedEdge(owner[f.ChildTable], owner[f.ParentTable])).Where(e => e.From != e.To).Distinct().ToArray(); }
    public IReadOnlyList<Scc> Components { get; } public IReadOnlyList<CondensedEdge> Edges { get; }
    public bool IsAcyclic() { var seen = 0; var pending = Components.ToDictionary(c => c.Id, _ => 0); foreach (var edge in Edges) pending[edge.To]++; var queue = new Queue<int>(pending.Where(x => x.Value == 0).Select(x => x.Key)); while (queue.TryDequeue(out var id)) { seen++; foreach (var edge in Edges.Where(x => x.From == id)) if (--pending[edge.To] == 0) queue.Enqueue(edge.To); } return seen == Components.Count; }
    public IReadOnlyList<IReadOnlyList<int>> TopologicalLayers() { var pending = Components.ToDictionary(c => c.Id, _ => 0); foreach (var edge in Edges) pending[edge.To]++; var layers = new List<IReadOnlyList<int>>(); var next = pending.Where(x => x.Value == 0).Select(x => x.Key).ToArray(); while (next.Length > 0) { layers.Add(next); next = next.SelectMany(id => Edges.Where(x => x.From == id)).Where(edge => --pending[edge.To] == 0).Select(edge => edge.To).ToArray(); } return layers; }
}
```

- [ ] **Step 4: Run them and confirm they pass**

Run: `dotnet test --filter "FullyQualifiedName~TarjanSccTests"`

Expected: `Passed: 5. Failed: 0.`

- [ ] **Step 5: Commit**

Run: `git add src/DataPitcher.Core/Graph/TarjanScc.cs src/DataPitcher.Core/Graph/CondensedGraph.cs tests/DataPitcher.UnitTests/Graph/TarjanSccTests.cs && git commit -m "feat: add SCC condensation"`

### Task 6: The closure store abstraction and an in-memory fake

**Files:**
- Create: `src/DataPitcher.Core/Closure/IClosureStore.cs`, `src/DataPitcher.Core/Closure/ClosureModels.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Closure/InMemoryClosureStore.cs`

- [ ] **Step 1: Write the failing store contract test and fake**

```csharp
using DataPitcher.Core.Closure; using DataPitcher.Core.Identity; using DataPitcher.Core.Schema; using Xunit;
namespace DataPitcher.UnitTests.Closure;
public sealed class InMemoryClosureStore : IClosureStore
{
    private readonly HashSet<RowAddress> _target = []; private readonly Dictionary<RowAddress, int> _generations = []; private readonly Dictionary<(ClosureRelationship, StableKey), List<StableKey>> _links = [];
    public void MarkTarget(TableDefinition table, params StableKey[] keys) { foreach (var key in keys) _target.Add(new(table, key)); }
    public void Link(ClosureRelationship relationship, params (StableKey From, StableKey To)[] pairs) { foreach (var pair in pairs) { var key = (relationship, pair.From); if (!_links.TryGetValue(key, out var values)) _links[key] = values = []; values.Add(pair.To); } }
    public Task<IReadOnlyCollection<StableKey>> SeedRootKeysAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, CancellationToken cancellationToken) => InsertAsync(table, keys, 0);
    public Task<IReadOnlyDictionary<StableKey, bool>> ProbeTargetAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<StableKey, bool>>(keys.ToDictionary(key => key, key => _target.Contains(new(table, key))));
    public Task<IReadOnlyCollection<StableKey>> ExpandAsync(ClosureRelationship relationship, IReadOnlyCollection<StableKey> fromKeys, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<StableKey>>(fromKeys.SelectMany(key => _links.GetValueOrDefault((relationship, key)) ?? []).Distinct().ToArray());
    public Task<IReadOnlyCollection<StableKey>> InsertNewKeysAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, int generation, CancellationToken cancellationToken) => InsertAsync(table, keys, generation);
    private Task<IReadOnlyCollection<StableKey>> InsertAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, int generation) => Task.FromResult<IReadOnlyCollection<StableKey>>(keys.Where(key => _generations.TryAdd(new(table, key), generation)).ToArray());
}
public sealed class InMemoryClosureStoreTests
{
    [Fact] public async Task InMemoryClosureStore_WhenKeyWasAlreadyStaged_ReturnsOnlyGenuinelyNewKeys()
    {
        var table = new TableDefinition("dbo", "T", [], null, []); var key = new StableKey([new KeyComponent("K", 1)]); var store = new InMemoryClosureStore();
        Assert.Single(await store.InsertNewKeysAsync(table, [key], 1, CancellationToken.None));
        Assert.Empty(await store.InsertNewKeysAsync(table, [key], 2, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run it and confirm it fails for the missing seam**

Run: `dotnet test --filter "FullyQualifiedName~InMemoryClosureStoreTests"`

Expected: compilation fails with `The type or namespace name 'Closure' does not exist in the namespace 'DataPitcher.Core'`.

- [ ] **Step 3: Write the four-operation seam and its models**

```csharp
using DataPitcher.Core.Identity; using DataPitcher.Core.Schema;
namespace DataPitcher.Core.Closure;
public interface IClosureStore
{
    Task<IReadOnlyCollection<StableKey>> SeedRootKeysAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<StableKey, bool>> ProbeTargetAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<StableKey>> ExpandAsync(ClosureRelationship relationship, IReadOnlyCollection<StableKey> fromKeys, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<StableKey>> InsertNewKeysAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, int generation, CancellationToken cancellationToken);
}
public enum RootConflictPolicy { FailOnConflict, SkipExisting, Upsert }
public sealed record ClosureRelationship(ForeignKeyDefinition ForeignKey, bool IsInbound = false, bool IsEnabled = true) { public TableDefinition FromTable => IsInbound ? ForeignKey.ParentTable : ForeignKey.ChildTable; public TableDefinition ToTable => IsInbound ? ForeignKey.ChildTable : ForeignKey.ParentTable; }
public sealed record ClosureRoot(TableDefinition Table, IReadOnlyCollection<StableKey> Keys, RootConflictPolicy ConflictPolicy);
public sealed record ClosureRequest(IReadOnlyCollection<ClosureRoot> Roots, IReadOnlyCollection<ClosureRelationship> Relationships);
public sealed record RowAddress(TableDefinition Table, StableKey Key);
public sealed record ClosureRow(TableDefinition Table, StableKey Key, int Generation);
public sealed class ClosureResult(IReadOnlyCollection<ClosureRow> rows) { public IReadOnlyCollection<ClosureRow> Rows { get; } = rows; public bool Contains(TableDefinition table, StableKey key) => Rows.Any(row => row.Table == table && row.Key == key); }
public sealed class RootConflictException(RowAddress row) : InvalidOperationException($"Root already exists: {row.Table.Schema}.{row.Table.Name}.");
```

The database-backed implementation is deferred to the Docker-blocked provider slice; this seam lets the algorithm be fully tested now.

- [ ] **Step 4: Run it and confirm the seam compiles**

Run: `dotnet test --filter "FullyQualifiedName~InMemoryClosureStoreTests"`

Expected: `Passed: 1. Failed: 0.`

- [ ] **Step 5: Commit**

Run: `git add src/DataPitcher.Core/Closure/IClosureStore.cs src/DataPitcher.Core/Closure/ClosureModels.cs tests/DataPitcher.UnitTests/Closure/InMemoryClosureStore.cs && git commit -m "feat: define closure store seam"`

### Task 7: The closure algorithm

**Files:**
- Create: `src/DataPitcher.Core/Closure/DependencyClosure.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Closure/DependencyClosureTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using DataPitcher.Core.Closure; using DataPitcher.Core.Identity; using DataPitcher.Core.Schema; using Xunit;
namespace DataPitcher.UnitTests.Closure;
public sealed class DependencyClosureTests
{
    [Fact] public async Task Closure_WhenChildSelected_IncludesMissingParent() { var c=T("C"); var p=T("P"); var e=E(F(c,p)); var s=new InMemoryClosureStore(); s.Link(e,(K(1),K(2))); var r=await Run(s,[e],Root(c,1)); Assert.True(r.Contains(p,K(2))); }
    [Fact] public async Task Closure_WhenParentSelected_ExcludesInboundChildrenByDefault() { var c=T("C"); var p=T("P"); var e=E(F(c,p)); var s=new InMemoryClosureStore(); s.Link(e,(K(1),K(2))); var r=await Run(s,[e],Root(p,2)); Assert.False(r.Contains(c,K(1))); }
    [Fact] public async Task Closure_WhenInboundRelationshipEnabled_IncludesChildren() { var c=T("C"); var p=T("P"); var e=E(F(c,p), true); var s=new InMemoryClosureStore(); s.Link(e,(K(2),K(1))); var r=await Run(s,[e],Root(p,2)); Assert.True(r.Contains(c,K(1))); }
    [Fact] public async Task Closure_WhenOptionalForeignKeyIsNull_AddsNoParent() { var c=T("C"); var p=T("P"); var e=E(F(c,p)); var s=new InMemoryClosureStore(); var r=await Run(s,[e],Root(c,1)); Assert.False(r.Contains(p,K(2))); }
    [Fact] public async Task Closure_WhenForeignKeyIsComposite_ResolvesParent() { var c=T("C"); var p=T("P"); var e=E(F(c,p, count:2)); var s=new InMemoryClosureStore(); s.Link(e,(K(1,2),K(7,8))); var r=await Run(s,[e],new ClosureRoot(c,[K(1,2)],RootConflictPolicy.FailOnConflict)); Assert.True(r.Contains(p,K(7,8))); }
    [Fact] public async Task Closure_WhenParentSharedByTwoChildren_IncludesParentOnce() { var c=T("C"); var p=T("P"); var e=E(F(c,p)); var s=new InMemoryClosureStore(); s.Link(e,(K(1),K(9)),(K(2),K(9))); var r=await Run(s,[e],new ClosureRoot(c,[K(1),K(2)],RootConflictPolicy.FailOnConflict)); Assert.Single(r.Rows.Where(x=>x.Table==p && x.Key==K(9))); }
    [Fact] public async Task Closure_WhenRootIsSkipExistingAndExists_ExpandsNothing() { var c=T("C"); var p=T("P"); var e=E(F(c,p)); var s=new InMemoryClosureStore(); s.MarkTarget(c,K(1)); s.Link(e,(K(1),K(2))); var r=await Run(s,[e],Root(c,1,RootConflictPolicy.SkipExisting)); Assert.False(r.Contains(c,K(1))); Assert.False(r.Contains(p,K(2))); }
    [Fact] public async Task Closure_WhenRootIsUpsertAndExists_ExpandsDependencies() { var c=T("C"); var p=T("P"); var e=E(F(c,p)); var s=new InMemoryClosureStore(); s.MarkTarget(c,K(1)); s.Link(e,(K(1),K(2))); var r=await Run(s,[e],Root(c,1,RootConflictPolicy.Upsert)); Assert.True(r.Contains(c,K(1))); Assert.True(r.Contains(p,K(2))); }
    [Fact] public async Task Closure_WhenParentSatisfiedBehindTrustedConstraint_TerminatesBranch() { var c=T("C"); var p=T("P"); var g=T("G"); var cp=E(F(c,p)); var pg=E(F(p,g)); var s=new InMemoryClosureStore(); s.MarkTarget(p,K(2)); s.Link(cp,(K(1),K(2))); s.Link(pg,(K(2),K(3))); var r=await Run(s,[cp,pg],Root(c,1)); Assert.False(r.Contains(p,K(2))); Assert.False(r.Contains(g,K(3))); }
    [Fact] public async Task Closure_WhenParentSatisfiedBehindUntrustedConstraint_TransfersParentAnyway() { var c=T("C"); var p=T("P"); var g=T("G"); var cp=E(F(c,p,trusted:false)); var pg=E(F(p,g)); var s=new InMemoryClosureStore(); s.MarkTarget(p,K(2)); s.Link(cp,(K(1),K(2))); s.Link(pg,(K(2),K(3))); var r=await Run(s,[cp,pg],Root(c,1)); Assert.True(r.Contains(p,K(2))); Assert.True(r.Contains(g,K(3))); }
    [Fact] public async Task Closure_WhenRelationshipDisabled_ContributesNoRows() { var c=T("C"); var p=T("P"); var e=new ClosureRelationship(F(c,p),false,false); var s=new InMemoryClosureStore(); s.Link(e,(K(1),K(2))); var r=await Run(s,[e],Root(c,1)); Assert.False(r.Contains(p,K(2))); }
    [Fact] public async Task Closure_WhenGraphHasTwoTableCycle_Terminates() { var a=T("A"); var b=T("B"); var ab=E(F(a,b)); var ba=E(F(b,a)); var s=new InMemoryClosureStore(); s.Link(ab,(K(1),K(2))); s.Link(ba,(K(2),K(1))); var r=await Run(s,[ab,ba],Root(a,1)); Assert.Equal(2,r.Rows.Count); }
    [Fact] public async Task Closure_WhenTableIsSelfReferencing_Terminates() { var eTable=T("E"); var e=E(F(eTable,eTable)); var s=new InMemoryClosureStore(); s.Link(e,(K(1),K(1))); var r=await Run(s,[e],Root(eTable,1)); Assert.Single(r.Rows); }

    private static TableDefinition T(string name) => new("dbo",name,[],new UniqueConstraint($"PK_{name}",["K1"]),[]);
    private static StableKey K(params object?[] values) => new(values.Select((value,index)=>new KeyComponent($"K{index+1}",value as IComparable ?? (IComparable?)value)));
    private static ForeignKeyDefinition F(TableDefinition child,TableDefinition parent,bool trusted=true,int count=1) { var columns=Enumerable.Range(1,count).Select(x=>$"K{x}").ToArray(); return new ForeignKeyDefinition($"FK_{child.Name}_{parent.Name}",child,parent,columns,columns,true,trusted); }
    private static ClosureRelationship E(ForeignKeyDefinition foreignKey,bool inbound=false) => new(foreignKey,inbound);
    private static ClosureRoot Root(TableDefinition table,int key,RootConflictPolicy policy=RootConflictPolicy.FailOnConflict) => new(table,[K(key)],policy);
    private static Task<ClosureResult> Run(InMemoryClosureStore store,ClosureRelationship[] relationships,params ClosureRoot[] roots) => new DependencyClosure(store).ComputeAsync(new ClosureRequest(roots,relationships),CancellationToken.None);
}
```

- [ ] **Step 2: Run them and confirm the right failure**

Run: `dotnet test --filter "FullyQualifiedName~DependencyClosureTests"`

Expected: compilation fails with `The type or namespace name 'DependencyClosure' could not be found`.

- [ ] **Step 3: Write the complete generation-loop implementation**

```csharp
using DataPitcher.Core.Identity; using DataPitcher.Core.Schema;
namespace DataPitcher.Core.Closure;
public sealed class DependencyClosure(IClosureStore store)
{
    private sealed record Frontier(TableDefinition Table, StableKey Key, RootConflictPolicy? RootPolicy, ClosureRelationship? Incoming);
    public async Task<ClosureResult> ComputeAsync(ClosureRequest request, CancellationToken cancellationToken)
    {
        var frontier = new List<Frontier>(); var included = new Dictionary<RowAddress, ClosureRow>();
        foreach (var root in request.Roots)
            foreach (var key in await store.SeedRootKeysAsync(root.Table, root.Keys, cancellationToken))
                frontier.Add(new(root.Table, key, root.ConflictPolicy, null));
        for (var generation = 0; frontier.Count > 0; generation++)
        {
            var expandable = new Dictionary<TableDefinition, List<StableKey>>();
            foreach (var group in frontier.GroupBy(x => x.Table))
            {
                var keys = group.Select(x => x.Key).Distinct().ToArray();
                var exists = await store.ProbeTargetAsync(group.Key, keys, cancellationToken);
                foreach (var item in group)
                {
                    var include = item.RootPolicy switch
                    {
                        RootConflictPolicy.FailOnConflict when exists[item.Key] => throw new RootConflictException(new(item.Table, item.Key)),
                        RootConflictPolicy.SkipExisting => !exists[item.Key],
                        RootConflictPolicy.Upsert => true,
                        null => !(exists[item.Key] && item.Incoming!.ForeignKey.IsEnforced && item.Incoming.ForeignKey.IsTrusted),
                        _ => true
                    };
                    if (!include) continue;
                    included.TryAdd(new(item.Table, item.Key), new(item.Table, item.Key, generation));
                    if (!expandable.TryGetValue(item.Table, out var keysToExpand)) expandable[item.Table] = keysToExpand = [];
                    keysToExpand.Add(item.Key);
                }
            }
            var discovered = new Dictionary<RowAddress, ClosureRelationship>();
            foreach (var relationship in request.Relationships.Where(x => x.IsEnabled))
            {
                if (!expandable.TryGetValue(relationship.FromTable, out var fromKeys)) continue;
                foreach (var key in await store.ExpandAsync(relationship, fromKeys.Distinct().ToArray(), cancellationToken)) discovered.TryAdd(new(relationship.ToTable, key), relationship);
            }
            frontier = [];
            foreach (var group in discovered.GroupBy(x => x.Key.Table))
            {
                var keys = group.Select(x => x.Key.Key).ToArray();
                foreach (var key in await store.InsertNewKeysAsync(group.Key, keys, generation + 1, cancellationToken))
                    frontier.Add(new(group.Key, key, null, discovered[new(group.Key, key)]));
            }
        }
        return new ClosureResult(included.Values);
    }
}
```

- [ ] **Step 4: Run them and confirm they pass**

Run: `dotnet test --filter "FullyQualifiedName~DependencyClosureTests"`

Expected: `Passed: 13. Failed: 0.`

- [ ] **Step 5: Add the diamond fixture — Closure_WhenParentSatisfiedOnOnePathButRequiredOnAnother_StillTransfersSharedAncestor**

```csharp
[Fact] public async Task Closure_WhenParentSatisfiedOnOnePathButRequiredOnAnother_StillTransfersSharedAncestor()
{
    var r=T("R"); var a=T("A"); var b=T("B"); var x=T("X"); var ra=E(F(r,a)); var rb=E(F(r,b)); var ax=E(F(a,x)); var bx=E(F(b,x));
    var s=new InMemoryClosureStore(); s.MarkTarget(a,K(2)); s.Link(ra,(K(1),K(2))); s.Link(rb,(K(1),K(3))); s.Link(ax,(K(2),K(4))); s.Link(bx,(K(3),K(4)));
    var result=await Run(s,[ra,rb,ax,bx],Root(r,1));
    Assert.True(result.Contains(x,K(4)));
}
```

This single test catches the two most dangerous errors: computing the final set by subtraction, and losing multi-path demand. Insert it before the fixture helpers in `DependencyClosureTests`.

- [ ] **Step 6: Run all closure tests and confirm they pass**

Run: `dotnet test --filter "FullyQualifiedName~DependencyClosureTests"`

Expected: `Passed: 14. Failed: 0.`

- [ ] **Step 7: Commit**

Run: `git add src/DataPitcher.Core/Closure/DependencyClosure.cs tests/DataPitcher.UnitTests/Closure/DependencyClosureTests.cs && git commit -m "feat: compute target-aware dependency closure"`

### Task 8: Architecture tests and the coverage gate

**Files:**
- Create: `tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj`, `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs`, `scripts/test-all.sh`, `scripts/test-all.ps1`
- Modify: `DataPitcher.sln`
- Test: `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs`

- [ ] **Step 1: Write the failing architecture tests**

```csharp
using System.Xml.Linq;
using Xunit;
namespace DataPitcher.ArchitectureTests;
public sealed class DependencyRuleTests
{
    [Fact] public void Core_ReferencesNoAspNetDataAccessOrProviderPackage() { var names=References(Project("DataPitcher.Core")).Concat(Packages(Project("DataPitcher.Core"))); Assert.DoesNotContain(names,name => name.StartsWith("Microsoft.AspNetCore",StringComparison.Ordinal) || name.StartsWith("DataPitcher.Providers.",StringComparison.Ordinal) || name is "Dapper" or "LinqToDB" or "Microsoft.EntityFrameworkCore" or "Npgsql" or "Microsoft.Data.SqlClient"); }
    [Fact] public void Nothing_ReferencesTheApiProject() => Assert.DoesNotContain(Projects().Where(p=>Name(p)!="DataPitcher.Api").SelectMany(References),name=>name=="DataPitcher.Api");
    [Fact] public void OnlyApi_ReferencesConcreteProviderProjects() => Assert.DoesNotContain(Projects().Where(p=>Name(p)!="DataPitcher.Api").SelectMany(References),name=>name.StartsWith("DataPitcher.Providers.",StringComparison.Ordinal));
    private static string Root { get; }=FindRoot();
    private static string Project(string name)=>Projects().Single(p=>Name(p)==name);
    private static IEnumerable<string> Projects()=>Directory.GetFiles(Root,"*.csproj",SearchOption.AllDirectories);
    private static string Name(string project)=>Path.GetFileNameWithoutExtension(project);
    private static IEnumerable<string> References(string project)=>XDocument.Load(project).Descendants("ProjectReference").Select(x=>Path.GetFileNameWithoutExtension(x.Attribute("Include")!.Value));
    private static IEnumerable<string> Packages(string project)=>XDocument.Load(project).Descendants("PackageReference").Select(x=>x.Attribute("Include")!.Value);
    private static string FindRoot() { for (var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent) if (File.Exists(Path.Combine(directory.FullName,"DataPitcher.sln"))) return directory.FullName; throw new DirectoryNotFoundException("DataPitcher.sln"); }
}
```

- [ ] **Step 2: Run them and confirm the right failure**

Run: `dotnet test tests/DataPitcher.ArchitectureTests --filter "FullyQualifiedName~DependencyRuleTests"`

Expected: `MSB1009: Project file does not exist.`

- [ ] **Step 3: Add the architecture project, solution entry, and coverage gates**

```xml
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" /><PackageReference Include="xunit" Version="2.9.3" /><PackageReference Include="xunit.runner.visualstudio" Version="3.1.4"><PrivateAssets>all</PrivateAssets></PackageReference><PackageReference Include="coverlet.collector" Version="6.0.4"><PrivateAssets>all</PrivateAssets></PackageReference></ItemGroup></Project>
```

Run `dotnet sln DataPitcher.sln add tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj`.

```bash
#!/usr/bin/env bash
set -euo pipefail
rm -rf artifacts/test-results
dotnet build DataPitcher.sln
dotnet test DataPitcher.sln --no-build
dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --no-build --collect:"XPlat Code Coverage" --results-directory artifacts/test-results -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
report="$(find artifacts/test-results -name coverage.opencover.xml -print -quit)"
test -n "$report"
for metric in SequenceCoverage BranchCoverage MethodCoverage; do
  value="$(/usr/bin/xmllint --xpath "string(/CoverageSession/Summary/@$metric)" "$report")"
  awk "BEGIN { exit ($value == 100 ? 0 : 1) }" || { echo "$metric is $value%, expected 100%"; exit 1; }
done
```

```powershell
$ErrorActionPreference = 'Stop'
Remove-Item artifacts/test-results -Recurse -Force -ErrorAction SilentlyContinue
dotnet build DataPitcher.sln
dotnet test DataPitcher.sln --no-build
dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --no-build --collect:'XPlat Code Coverage' --results-directory artifacts/test-results -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
$report = (Get-ChildItem artifacts/test-results -Filter coverage.opencover.xml -Recurse | Select-Object -First 1).FullName
if (-not $report) { throw 'Coverlet did not write coverage.opencover.xml.' }
[xml]$coverage = Get-Content $report
foreach ($metric in 'SequenceCoverage','BranchCoverage','MethodCoverage') { $value = [double]$coverage.CoverageSession.Summary.GetAttribute($metric); if ($value -ne 100) { throw "$metric is $value%, expected 100%." } }
```

- [ ] **Step 4: Run the architecture tests and confirm they pass**

Run: `dotnet test tests/DataPitcher.ArchitectureTests --filter "FullyQualifiedName~DependencyRuleTests"`

Expected: `Passed: 3. Failed: 0.`

- [ ] **Step 5: Prove the coverage gate is effective, then restore the test**

Copy `tests/DataPitcher.UnitTests/Closure/DependencyClosureTests.cs` to `/tmp/DependencyClosureTests.cs`, delete the complete `Closure_WhenRelationshipDisabled_ContributesNoRows` test shown in Task 7, then run: `./scripts/test-all.sh`

Expected: non-zero exit with `BranchCoverage is` below `100%, expected 100%` because the disabled-relationship branch is no longer exercised. Restore with: `cp /tmp/DependencyClosureTests.cs tests/DataPitcher.UnitTests/Closure/DependencyClosureTests.cs`, then run `./scripts/test-all.sh` and expect successful build, tests, and all three metrics at 100%.

- [ ] **Step 6: Commit**

Run: `git add DataPitcher.sln tests/DataPitcher.ArchitectureTests scripts/test-all.sh scripts/test-all.ps1 && git commit -m "test: enforce architecture and full coverage"`

## Self-Review

Slice 1 covers provider-free stable identity, schema metadata, dependency graph topology, SCC condensation, target-aware demand-driven closure, unit fixtures, project-boundary checks, and a 100% coverage gate. It deliberately defers database-backed closure storage, provider catalog discovery, target-constraint inspection, integration environments, API, transfer writing, Docker Compose, Testcontainers, and Playwright because Docker is unavailable and those require a server or network.

Type and method-name consistency was checked across all eight tasks: later tasks use the earlier `StableKey`, schema records, graph APIs, `ClosureRelationship`, `IClosureStore` four methods, closure models, and test helper signatures exactly as defined.
