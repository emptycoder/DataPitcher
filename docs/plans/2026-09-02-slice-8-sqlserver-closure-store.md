# DataPitcher Slice 8: SQL Server-Backed Closure Store Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a SQL Server-backed `IClosureStore` so Slice 1’s thirty-one closure behavioural tests re-run with unchanged assertions against independent real SQL Server source and target databases.

**Architecture:** Add the narrow `DataPitcher.Providers.SqlServer` provider project: a three-query catalog snapshot supplies ordered schema facts, a plan-owned typed staging manager owns source, input, and target key tables, and `SqlServerClosureStore` implements the existing four-operation Core seam. Source expansion remains set-based in the source database; candidate stable keys are copied to target-owned staging with `SqlBulkCopy` before one target-side join probe per frontier table and generation. `DependencyClosure` and transfer execution remain unchanged.

**Tech Stack:** .NET 10/C# latest; `Microsoft.Data.SqlClient` **7.0.2**; `Testcontainers.MsSql` **4.14.0**; `mcr.microsoft.com/mssql/server:2022-latest`; xUnit; Coverlet; ReportGenerator; SQL Server `sys` catalogs; native `SqlBulkCopy`.

---

## File Structure

- `DataPitcher.sln` — register the SQL Server provider and integration-test projects.
- `src/DataPitcher.Providers.SqlServer/DataPitcher.Providers.SqlServer.csproj` — Core reference and pinned SqlClient 7.0.2.
- `src/DataPitcher.Providers.SqlServer/SqlServerCatalogReader.cs` — three-query catalog snapshot, native column metadata, ordered keys, and FK enforcement/trust facts.
- `src/DataPitcher.Providers.SqlServer/SqlServerIdentifier.cs` — bracket identifier quoting for all generated and catalog-derived identifiers.
- `src/DataPitcher.Providers.SqlServer/SqlServerStagingTables.cs` — generated typed key stages and direct `SqlBulkCopy` movement between databases.
- `src/DataPitcher.Providers.SqlServer/SqlServerClosureStore.cs` — the set-based, four-method `IClosureStore` implementation.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj` — pinned SQL Server integration packages and provider reference.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerClosureFixture.cs` — serialized two-container fixture, disposable databases, and deterministic schema.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerClosureFixtureTests.cs` — fixture and untrusted-state proof.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerCatalogReaderTests.cs` — ordered metadata, CLR type/nullability, and three-command catalog proof.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerIdentifierTests.cs` — real-server closing-bracket round trip.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerStagingTablesTests.cs` — native typed stages, complete-key uniqueness, naming, and immutable generations.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerClosureStoreTests.cs` — direct positional expansion and target-probe semantics.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerDependencyClosureTests.cs` — the unchanged thirty-one behavioural assertions against SQL Server.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerWireCommandRecorder.cs` — server-side Extended Events recorder for actual RPC commands.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerProbeBatchingTests.cs` — wire-level one-probe-per-frontier proof.
- `scripts/test-sqlserver.sh` — SQL Server integration lane, deliberately without a coverage gate.

## Scope and Deferrals

This slice proves catalog discovery, quoting, typed staging, expansion, target constraint-state probing, and the behavioural re-run against SQL Server. It excludes transfer execution/writing/verification, cycle-strategy execution, staging TTLs, broad dialect support, and LINQ to DB bulk copy.

ADR 0005 is binding: do not add LINQ to DB or use its advisory `BulkCopyAsync`, which can silently degrade and cannot report its strategy. All candidate-key movement, including the cross-database target bridge, uses `Microsoft.Data.SqlClient.SqlBulkCopy` directly. This is staging transport, not the deferred transfer-write feature.

**Verified environment.** Docker Engine 29.3.1 runs on an arm64 Apple Silicon host with 7.65 GiB available. Microsoft publishes no native arm64 SQL Server image; `mcr.microsoft.com/mssql/server:2022-latest` is amd64 only. It nevertheless runs under Apple Silicon binary translation and served real queries when measured. Thus tests run under emulation. Memory has been measured, not estimated: a single container uses 1.08 GiB at readiness (8.7s), and two run simultaneously at 2.28 GiB total (second ready in 7.1s), 29.8 percent of the 7.65 GiB allocation, with adequate headroom based on this idle and light-load evidence. Use one non-parallel xUnit collection fixture with exactly two containers; tests create databases inside them.

Line, branch, and method coverage must each be 100 percent after ReportGenerator merges OpenCover reports; only `scripts/test-all.sh` enforces that gate. `scripts/test-sqlserver.sh` mirrors `scripts/test-postgres.sh` without a threshold because no individual lane instruments the solution. Warnings remain errors through `Directory.Build.props`.

ADR 0003 is binding. Prune only behind a present, enabled, trusted target FK: `is_disabled = 0` and `is_not_trusted = 0`. `NOCHECK CONSTRAINT` gives `(1,1)`; `CHECK CONSTRAINT` without validation gives `(0,1)`; only `WITH CHECK CHECK CONSTRAINT` restores `(0,0)`. The seeded target uses `WITH NOCHECK ADD CONSTRAINT`, producing reachable enabled/untrusted `(0,1)` warning and non-pruning paths.

### Task 1: Add the SQL Server integration lane and project shells

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/DataPitcher.Providers.SqlServer.csproj`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj`, `scripts/test-sqlserver.sh`
- Modify: `DataPitcher.sln`
- Test: `scripts/test-sqlserver.sh`

- [ ] **Step 1: Write the failing lane-presence check.**

```bash
test -x scripts/test-sqlserver.sh
```

- [ ] **Step 2: Run the check and confirm it is red.**

Run: `test -x scripts/test-sqlserver.sh`

Expected: exit 1 because the lane does not exist.

- [ ] **Step 3: Create the minimal provider/test shells, solution entries, and lane.**

```xml
<!-- src/DataPitcher.Providers.SqlServer/DataPitcher.Providers.SqlServer.csproj -->
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>DataPitcher.Providers.SqlServer</RootNamespace></PropertyGroup><ItemGroup><ProjectReference Include="../DataPitcher.Core/DataPitcher.Core.csproj" /><PackageReference Include="Microsoft.Data.SqlClient" Version="7.0.2" /></ItemGroup></Project>
```

```xml
<!-- tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj -->
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><ProjectReference Include="../../src/DataPitcher.Providers.SqlServer/DataPitcher.Providers.SqlServer.csproj" /><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" /><PackageReference Include="xunit" Version="2.9.3" /><PackageReference Include="xunit.runner.visualstudio" Version="3.1.4"><PrivateAssets>all</PrivateAssets></PackageReference><PackageReference Include="coverlet.collector" Version="6.0.4"><PrivateAssets>all</PrivateAssets></PackageReference><PackageReference Include="Microsoft.Data.SqlClient" Version="7.0.2" /><PackageReference Include="Testcontainers.MsSql" Version="4.14.0" /></ItemGroup></Project>
```

```bash
#!/usr/bin/env bash
# scripts/test-sqlserver.sh
set -euo pipefail
dotnet build tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj
dotnet test tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj --no-build "$@"
```

Run `dotnet sln DataPitcher.sln add src/DataPitcher.Providers.SqlServer/DataPitcher.Providers.SqlServer.csproj tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj`, then make the lane executable. Do not alter `scripts/test-all.sh`; its solution-wide ReportGenerator merge includes registered projects.

- [ ] **Step 4: Run the lane and confirm the shell is usable.**

Run: `./scripts/test-sqlserver.sh`

Expected: exit 0; build succeeds and the test runner reports no tests until Task 2.

- [ ] **Step 5: Commit the lane and pinned shells.**

Run: `git add DataPitcher.sln src/DataPitcher.Providers.SqlServer/DataPitcher.Providers.SqlServer.csproj tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj scripts/test-sqlserver.sh && git commit -m "test: add sql server integration lane"`

### Task 2: Seed serialized, separate SQL Server source and target databases

**Files:**
- Create: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerClosureFixture.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerClosureFixtureTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerClosureFixtureTests.cs`

- [ ] **Step 1: Write the failing fixture contract.**

```csharp
using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[Collection("SqlServer closure")]
public sealed class SqlServerClosureFixtureTests(SqlServerClosureFixture fixture)
{
    [Fact] public async Task Scope_WhenCreated_HasAllShapesAndEnabledUntrustedTargetForeignKey()
    {
        await using var scope = await fixture.CreateScopeAsync();
        Assert.Equal(16, await scope.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo');"));
        Assert.Equal(0, await scope.ScalarTargetAsync<int>("SELECT is_disabled FROM sys.foreign_keys WHERE name = N'Target_FK_P_G';"));
        Assert.Equal(1, await scope.ScalarTargetAsync<int>("SELECT is_not_trusted FROM sys.foreign_keys WHERE name = N'Target_FK_P_G';"));
    }
}
```

- [ ] **Step 2: Run it and confirm the fixture is absent.**

Run: `dotnet test tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj --filter "FullyQualifiedName~SqlServerClosureFixtureTests"`

Expected: compilation fails with `The type or namespace name 'SqlServerClosureFixture' could not be found`.

- [ ] **Step 3: Implement the two-container collection fixture and complete schema.**

```csharp
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[CollectionDefinition("SqlServer closure", DisableParallelization = true)]
public sealed class SqlServerClosureCollection : ICollectionFixture<SqlServerClosureFixture> { }
public sealed class SqlServerClosureFixture : IAsyncLifetime
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-latest";
    private readonly MsSqlContainer _source = new MsSqlBuilder(Image).WithPassword("DataPitcher!Sql2026").Build();
    private readonly MsSqlContainer _target = new MsSqlBuilder(Image).WithPassword("DataPitcher!Sql2026").Build();
    public async Task InitializeAsync() { await _source.StartAsync(); await _target.StartAsync(); }
    public async Task DisposeAsync() { await _source.DisposeAsync(); await _target.DisposeAsync(); }
    public async Task<SqlServerClosureScope> CreateScopeAsync()
    {
        var database = "dp_" + Guid.NewGuid().ToString("N");
        await CreateDatabaseAsync(_source.GetConnectionString(), database); await CreateDatabaseAsync(_target.GetConnectionString(), database);
        var source = WithDatabase(_source.GetConnectionString(), database); var target = WithDatabase(_target.GetConnectionString(), database);
        await SqlServerClosureScope.CreateAsync(source, false); await SqlServerClosureScope.CreateAsync(target, true);
        return new(database, source, target, _source.GetConnectionString(), _target.GetConnectionString());
    }
    private static string WithDatabase(string connectionString, string database) => new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database, TrustServerCertificate = true }.ConnectionString;
    private static async Task CreateDatabaseAsync(string connectionString, string database)
    { await using var c = new SqlConnection(connectionString); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = "CREATE DATABASE " + Quote(database) + ";"; await q.ExecuteNonQueryAsync(); }
    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
public sealed class SqlServerClosureScope(string database, string source, string target, string sourceAdmin, string targetAdmin) : IAsyncDisposable
{
    public string Database { get; } = database; public string SourceConnectionString { get; } = source; public string TargetConnectionString { get; } = target;
    public string SourceAdminConnectionString { get; } = sourceAdmin; public string TargetAdminConnectionString { get; } = targetAdmin;
    public static async Task CreateAsync(string connectionString, bool target) { foreach (var sql in SchemaSql(target)) await ExecuteOnAsync(connectionString, sql); }
    public Task ExecuteAsync(string sql) => ExecuteOnAsync(SourceConnectionString, sql); public Task ExecuteTargetAsync(string sql) => ExecuteOnAsync(TargetConnectionString, sql);
    public Task<T> ScalarAsync<T>(string sql) => ScalarOnAsync<T>(SourceConnectionString, sql); public Task<T> ScalarTargetAsync<T>(string sql) => ScalarOnAsync<T>(TargetConnectionString, sql);
    public async ValueTask DisposeAsync() { await DropAsync(SourceAdminConnectionString); await DropAsync(TargetAdminConnectionString); }
    private async Task DropAsync(string admin) { await using var c = new SqlConnection(admin); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = $"ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}];"; await q.ExecuteNonQueryAsync(); }
    private static async Task ExecuteOnAsync(string cs, string sql) { await using var c = new SqlConnection(cs); await c.OpenAsync(); await using var q = new SqlCommand(sql, c); await q.ExecuteNonQueryAsync(); }
    private static async Task<T> ScalarOnAsync<T>(string cs, string sql) { await using var c = new SqlConnection(cs); await c.OpenAsync(); await using var q = new SqlCommand(sql, c); return (T)(await q.ExecuteScalarAsync())!; }
    private static IEnumerable<string> SchemaSql(bool target) =>
    [
        "CREATE TABLE dbo.customers (customer_id int NOT NULL PRIMARY KEY, external_code nvarchar(64) NOT NULL UNIQUE)",
        "CREATE TABLE dbo.orders (order_id int NOT NULL PRIMARY KEY, customer_id int NOT NULL REFERENCES dbo.customers(customer_id))",
        "CREATE TABLE dbo.order_lines (line_id int NOT NULL PRIMARY KEY, order_id int NOT NULL REFERENCES dbo.orders(order_id))",
        "CREATE TABLE dbo.declared_key (physical_first int NOT NULL, physical_second int NOT NULL, CONSTRAINT PK_declared_key PRIMARY KEY (physical_second, physical_first))",
        "CREATE TABLE dbo.composite_parent (left_value int NOT NULL, right_value int NOT NULL, PRIMARY KEY (left_value, right_value))",
        "CREATE TABLE dbo.composite_child (id int NOT NULL PRIMARY KEY, child_left int NOT NULL, child_right int NOT NULL, CONSTRAINT FK_composite_child_parent FOREIGN KEY (child_right, child_left) REFERENCES dbo.composite_parent(left_value, right_value))",
        "CREATE TABLE dbo.optional_orders (id int NOT NULL PRIMARY KEY, customer_id int NULL REFERENCES dbo.customers(customer_id))",
        "CREATE TABLE dbo.external_parents (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL UNIQUE)", "CREATE TABLE dbo.external_children (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL REFERENCES dbo.external_parents(code))",
        "CREATE TABLE dbo.employees (id int NOT NULL PRIMARY KEY, manager_id int NULL REFERENCES dbo.employees(id))", "CREATE TABLE dbo.cycle_a (id int NOT NULL PRIMARY KEY, b_id int NULL)", "CREATE TABLE dbo.cycle_b (id int NOT NULL PRIMARY KEY, a_id int NULL REFERENCES dbo.cycle_a(id))", "ALTER TABLE dbo.cycle_a ADD CONSTRAINT FK_cycle_a_b FOREIGN KEY (b_id) REFERENCES dbo.cycle_b(id)",
        "CREATE TABLE dbo.unique_only (code nvarchar(64) NOT NULL UNIQUE)", "CREATE TABLE dbo.no_stable_key (value nvarchar(64) NULL)", "CREATE TABLE dbo.untrusted_grandparents (id int NOT NULL PRIMARY KEY)", "CREATE TABLE dbo.untrusted_parents (id int NOT NULL PRIMARY KEY, grandparent_id int NOT NULL)",
        target ? "INSERT dbo.untrusted_parents VALUES (2,3); ALTER TABLE dbo.untrusted_parents WITH NOCHECK ADD CONSTRAINT Target_FK_P_G FOREIGN KEY (grandparent_id) REFERENCES dbo.untrusted_grandparents(id)" : "ALTER TABLE dbo.untrusted_parents WITH CHECK ADD CONSTRAINT FK_P_G FOREIGN KEY (grandparent_id) REFERENCES dbo.untrusted_grandparents(id)"
    ];
}
```

The fixture covers simple and declaration-order composite primary keys, composite/nullable/unique-reference FKs, self-reference, a two-table cycle, unique-only, and keyless tables. The target inserts its violation before `WITH NOCHECK ADD CONSTRAINT`, therefore it is enabled/untrusted, not disabled. Keep the collection annotation on every test class.

- [ ] **Step 4: Run the fixture test and confirm it passes.**

Run: `dotnet test tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj --filter "FullyQualifiedName~SqlServerClosureFixtureTests"`

Expected: one passing test; two SQL Server 2022 containers serve queries, each scope has sixteen tables, and target `Target_FK_P_G` reports `is_disabled=0`, `is_not_trusted=1`.

- [ ] **Step 5: Commit the constrained fixture.**

Run: `git add tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerClosureFixture.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerClosureFixtureTests.cs && git commit -m "test: add seeded sql server closure fixture"`

### Task 3: Read SQL Server catalog facts in exactly three bounded queries

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerCatalogReader.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerCatalogReaderTests.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerWireCommandRecorder.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerCatalogReaderTests.cs`

- [ ] **Step 1: Write the failing snapshot and bounded-command tests.**

```csharp
using DataPitcher.Providers.SqlServer; using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[Collection("SqlServer closure")]
public sealed class SqlServerCatalogReaderTests(SqlServerClosureFixture fixture)
{
    [Fact] public async Task ReadAsync_UsesConstraintOrderAndReadsTrustInThreeCommands()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await using var wire = await SqlServerWireCommandRecorder.StartAsync(scope.SourceAdminConnectionString, "DataPitcher.Catalog");
        var source = await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None);
        var target = await new SqlServerCatalogReader(scope.TargetConnectionString).ReadAsync("dbo", CancellationToken.None);
        Assert.Equal(["physical_second", "physical_first"], source.Table("declared_key").Definition.PrimaryKey!.Columns);
        Assert.Equal(typeof(int), source.Table("optional_orders").Column("customer_id").ClrType); Assert.True(source.Table("optional_orders").Column("customer_id").IsNullable);
        Assert.Null(source.Table("unique_only").Definition.PrimaryKey); Assert.Equal(["code"], Assert.Single(source.Table("unique_only").Definition.UniqueConstraints).Columns);
        Assert.Equal(["child_right", "child_left"], source.ForeignKey("FK_composite_child_parent").ChildColumns); Assert.Equal(["left_value", "right_value"], source.ForeignKey("FK_composite_child_parent").ParentColumns);
        Assert.True(target.ForeignKey("Target_FK_P_G").IsEnforced); Assert.False(target.ForeignKey("Target_FK_P_G").IsTrusted); Assert.Equal(3, await wire.Count("DataPitcher.Catalog"));
    }
}
```

The source read’s three tagged server-observed commands prove the bounded contract independently of the sixteen-table fixture. Add an unmapped `decimal` test that asserts `NotSupportedException` from `ReadAsync`.

- [ ] **Step 2: Run it and confirm the catalog types are absent.**

Run: `dotnet test tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj --filter "FullyQualifiedName~SqlServerCatalogReaderTests"`

Expected: compilation fails with `The type or namespace name 'SqlServerCatalogReader' could not be found`.

- [ ] **Step 3: Implement the snapshot and server-side command recorder.**

```csharp
using DataPitcher.Core.Schema; using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer;
public sealed record SqlServerColumn(string Name, string StoreType, Type ClrType, bool IsNullable);
public sealed record SqlServerTable(TableDefinition Definition, IReadOnlyList<SqlServerColumn> Columns) { public SqlServerColumn Column(string name) => Columns.Single(c => c.Name == name); }
public sealed class SqlServerSchemaSnapshot(IEnumerable<SqlServerTable> tables, IEnumerable<ForeignKeyDefinition> foreignKeys)
{ public IReadOnlyList<SqlServerTable> Tables { get; } = tables.ToArray(); public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; } = foreignKeys.ToArray(); public SqlServerTable Table(string name) => Tables.Single(t => t.Definition.Name == name); public ForeignKeyDefinition ForeignKey(string name) => ForeignKeys.Single(f => f.Name == name); }
public sealed class SqlServerCatalogReader(string connectionString)
{
    private const string ColumnsSql = "/* DataPitcher.Catalog.Columns */ SELECT t.name,c.name,ty.name,c.max_length,c.is_nullable FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE s.name=@schema ORDER BY t.name,c.column_id";
    private const string KeysSql = "/* DataPitcher.Catalog.Keys */ SELECT t.name,k.name,k.type,c.name,i.key_ordinal FROM sys.key_constraints k JOIN sys.tables t ON t.object_id=k.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.index_columns i ON i.object_id=k.parent_object_id AND i.index_id=k.unique_index_id JOIN sys.columns c ON c.object_id=i.object_id AND c.column_id=i.column_id WHERE s.name=@schema AND i.key_ordinal>0 ORDER BY t.name,k.name,i.key_ordinal";
    private const string ForeignKeysSql = "/* DataPitcher.Catalog.ForeignKeys */ SELECT f.name,ct.name,pt.name,cc.name,pc.name,x.constraint_column_id,f.is_disabled,f.is_not_trusted FROM sys.foreign_keys f JOIN sys.tables ct ON ct.object_id=f.parent_object_id JOIN sys.schemas s ON s.schema_id=ct.schema_id JOIN sys.tables pt ON pt.object_id=f.referenced_object_id JOIN sys.foreign_key_columns x ON x.constraint_object_id=f.object_id JOIN sys.columns cc ON cc.object_id=x.parent_object_id AND cc.column_id=x.parent_column_id JOIN sys.columns pc ON pc.object_id=x.referenced_object_id AND pc.column_id=x.referenced_column_id WHERE s.name=@schema ORDER BY f.object_id,x.constraint_column_id";
    public async Task<SqlServerSchemaSnapshot> ReadAsync(string schema, CancellationToken ct)
    {
        var columns = await ReadColumnsAsync(schema, ct); var keys = await ReadKeysAsync(schema, columns.Keys, ct);
        var definitions = columns.ToDictionary(x => x.Key, x => new TableDefinition(schema, x.Key, x.Value.Select(c => new ColumnDefinition(c.Name, c.ClrType, c.IsNullable)).ToArray(), keys[x.Key].Primary, keys[x.Key].Unique));
        return new(definitions.Values.Select(d => new SqlServerTable(d, columns[d.Name])), await ReadForeignKeysAsync(schema, definitions, ct));
    }
    private async Task<Dictionary<string,List<SqlServerColumn>>> ReadColumnsAsync(string schema, CancellationToken ct) { var r = new Dictionary<string,List<SqlServerColumn>>(); await using var c=await OpenAsync(ct); await using var q = Command(c,ColumnsSql,schema); await using var rows = await q.ExecuteReaderAsync(ct); while (await rows.ReadAsync(ct)) { var table=rows.GetString(0); if(!r.TryGetValue(table,out var list)) r[table]=list=[]; list.Add(new(rows.GetString(1), StoreType(rows.GetString(2),rows.GetInt16(3)), Map(rows.GetString(2)),rows.GetBoolean(4))); } return r; }
    private async Task<Dictionary<string,(UniqueConstraint? Primary,List<UniqueConstraint> Unique)>> ReadKeysAsync(string schema, IEnumerable<string> tables, CancellationToken ct) { var r=tables.ToDictionary(t=>t,_=>((UniqueConstraint?)null,new List<UniqueConstraint>())); await using var c=await OpenAsync(ct); await using var q=Command(c,KeysSql,schema); await using var rows=await q.ExecuteReaderAsync(ct); var groups=new List<(string Table,string Name,string Type,List<string> Columns)>(); while(await rows.ReadAsync(ct)){var g=groups.LastOrDefault(x=>x.Table==rows.GetString(0)&&x.Name==rows.GetString(1)); if(g.Columns is null){g=(rows.GetString(0),rows.GetString(1),rows.GetString(2),[]);groups.Add(g);} g.Columns.Add(rows.GetString(3));} foreach(var g in groups){var k=new UniqueConstraint(g.Name,g.Columns);var prior=r[g.Table];r[g.Table]=g.Type=="PK"?(k,prior.Unique):(prior.Primary,[..prior.Unique,k]);} return r; }
    private async Task<List<ForeignKeyDefinition>> ReadForeignKeysAsync(string schema, IReadOnlyDictionary<string,TableDefinition> tables, CancellationToken ct) { var groups=new List<(string Name,string Child,string Parent,List<string> ChildColumns,List<string> ParentColumns,bool Disabled,bool NotTrusted)>(); await using var c=await OpenAsync(ct); await using var q=Command(c,ForeignKeysSql,schema); await using var rows=await q.ExecuteReaderAsync(ct); while(await rows.ReadAsync(ct)){var g=groups.LastOrDefault(x=>x.Name==rows.GetString(0));if(g.ChildColumns is null){g=(rows.GetString(0),rows.GetString(1),rows.GetString(2),[],[],rows.GetBoolean(6),rows.GetBoolean(7));groups.Add(g);}g.ChildColumns.Add(rows.GetString(3));g.ParentColumns.Add(rows.GetString(4));}return groups.Select(g=>new ForeignKeyDefinition(g.Name,tables[g.Child],tables[g.Parent],g.ChildColumns,g.ParentColumns,!g.Disabled,!g.NotTrusted)).ToList(); }
    private async Task<SqlConnection> OpenAsync(CancellationToken ct) { var c=new SqlConnection(connectionString); await c.OpenAsync(ct); return c; }
    private static SqlCommand Command(SqlConnection c,string sql,string schema) { var q=new SqlCommand(sql,c);q.Parameters.AddWithValue("@schema",schema);return q; }
    private static Type Map(string type) => type switch { "int" => typeof(int), "nvarchar" => typeof(string), _ => throw new NotSupportedException($"SQL Server type '{type}' is not mapped.") };
    private static string StoreType(string type,short length) => type == "nvarchar" ? $"nvarchar({length / 2})" : type;
}
```

Each reader owns and disposes its connection. Preserve the three SQL statements and their `constraint_column_id`/`key_ordinal` ordering. The count is always three: columns, primary/unique constraints, and foreign keys. `IsTrusted` is precisely `!is_not_trusted`, never inferred from enforcement.

```csharp
using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
public sealed class SqlServerWireCommandRecorder : IAsyncDisposable
{
    private readonly string _admin; private readonly string _name;
    private SqlServerWireCommandRecorder(string admin,string name) { _admin=admin; _name=name; }
    public static async Task<SqlServerWireCommandRecorder> StartAsync(string admin,string tag) { var r=new SqlServerWireCommandRecorder(admin,"dp_xe_"+Guid.NewGuid().ToString("N")); await r.ExecuteAsync($"CREATE EVENT SESSION [{r._name}] ON SERVER ADD EVENT sqlserver.rpc_completed(ACTION(sqlserver.sql_text) WHERE ([sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%{tag}%'))) ADD TARGET package0.ring_buffer; ALTER EVENT SESSION [{r._name}] ON SERVER STATE=START;"); return r; }
    public async Task<IReadOnlyList<string>> SqlTextsAsync() { await using var c=new SqlConnection(_admin);await c.OpenAsync();await using var q=new SqlCommand("SELECT n.e.value('(/event/action[@name=\"sql_text\"]/value)[1]','nvarchar(max)') FROM sys.dm_xe_session_targets t JOIN sys.dm_xe_sessions s ON s.address=t.event_session_address CROSS APPLY (SELECT CAST(t.target_data AS xml) x) d CROSS APPLY d.x.nodes('/RingBufferTarget/event') n(e) WHERE s.name=@name",c);q.Parameters.AddWithValue("@name",_name);await using var rows=await q.ExecuteReaderAsync();var texts=new List<string>();while(await rows.ReadAsync())texts.Add(rows.GetString(0));return texts; }
    public async Task<int> Count(string tag) => (await SqlTextsAsync()).Count(x=>x.Contains(tag,StringComparison.Ordinal));
    private async Task ExecuteAsync(string sql) { await using var c=new SqlConnection(_admin);await c.OpenAsync();await using var q=new SqlCommand(sql,c);await q.ExecuteNonQueryAsync(); }
    public async ValueTask DisposeAsync() { await ExecuteAsync($"ALTER EVENT SESSION [{_name}] ON SERVER STATE=STOP; DROP EVENT SESSION [{_name}] ON SERVER;"); }
}
```

This recorder queries SQL Server’s Extended Events ring buffer, counting received RPCs rather than store invocations or client logging. Its generated bracket-quoted session uses only the administrative connection.

- [ ] **Step 4: Run the catalog tests and confirm them green.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerCatalogReaderTests"`

Expected: two passing tests; ordered metadata and CLR/nullability match, the target is enforced/untrusted, an unmapped type fails clearly, and Extended Events reports three source catalog RPCs.

- [ ] **Step 5: Commit catalog discovery.**

Run: `git add src/DataPitcher.Providers.SqlServer/SqlServerCatalogReader.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerCatalogReaderTests.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerWireCommandRecorder.cs && git commit -m "feat: read sql server closure metadata"`

### Task 4: Bracket-quote SQL Server identifiers against a real server

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerIdentifier.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerIdentifierTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerIdentifierTests.cs`

- [ ] **Step 1: Write the failing closing-bracket round-trip test.**

```csharp
using DataPitcher.Providers.SqlServer; using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[Collection("SqlServer closure")]
public sealed class SqlServerIdentifierTests(SqlServerClosureFixture fixture)
{
    [Fact] public async Task Quote_ExecutesAnIdentifierContainingTheClosingBracketInjectionCharacter()
    { await using var scope=await fixture.CreateScopeAsync(); const string table="Select]Rows"; var q=SqlServerIdentifier.Qualified("dbo",table); await scope.ExecuteAsync($"CREATE TABLE {q} ([Value] int NOT NULL PRIMARY KEY); INSERT {q} ([Value]) VALUES (1);"); Assert.Equal(1,await scope.ScalarAsync<int>($"SELECT COUNT(*) FROM {q};")); }
}
```

- [ ] **Step 2: Run it and confirm the quoter is absent.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerIdentifierTests"`

Expected: compilation fails with `The name 'SqlServerIdentifier' does not exist in the current context`.

- [ ] **Step 3: Implement the minimal bracket quoter.**

```csharp
namespace DataPitcher.Providers.SqlServer;
public static class SqlServerIdentifier
{
    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    public static string Qualified(string schema, string name) => Quote(schema) + "." + Quote(name);
}
```

Use it for every catalog-derived schema, table, column, and constraint name and for every generated staging, index, database, and Extended Events session name. Bind values as `SqlParameter` values; never treat quoting as value escaping.

- [ ] **Step 4: Run the round trip and confirm it passes.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerIdentifierTests"`

Expected: one passing test that creates, inserts into, and selects from the real `[Select]]Rows]` table; string comparison alone is not accepted as proof.

- [ ] **Step 5: Commit identifier safety.**

Run: `git add src/DataPitcher.Providers.SqlServer/SqlServerIdentifier.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerIdentifierTests.cs && git commit -m "feat: quote sql server identifiers"`

### Task 5: Stage native SQL Server stable keys with direct bulk copy

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerStagingTables.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerStagingTablesTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerStagingTablesTests.cs`

- [ ] **Step 1: Write the failing typed-stage and immutable-generation test.**

```csharp
using DataPitcher.Core.Identity; using DataPitcher.Core.Schema; using DataPitcher.Providers.SqlServer; using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[Collection("SqlServer closure")]
public sealed class SqlServerStagingTablesTests(SqlServerClosureFixture fixture)
{
    [Fact] public async Task Stages_UseNativeKeyTypesCompleteUniqueIndexAndFirstGeneration()
    { await using var scope=await fixture.CreateScopeAsync(); var schema=await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo",CancellationToken.None); var table=schema.Table("declared_key").Definition; var keys=new Dictionary<TableDefinition,StableKeySelection>{{table,StableKeySelector.Select(table,null)}}; await using var first=new SqlServerStagingTables(scope.SourceConnectionString,scope.TargetConnectionString,schema,keys); await using var second=new SqlServerStagingTables(scope.SourceConnectionString,scope.TargetConnectionString,schema,keys); var key=new StableKey([new("physical_second",2),new("physical_first",1)]); Assert.Single(await first.InsertSourceAsync(table,[key],1,CancellationToken.None)); Assert.Empty(await first.InsertSourceAsync(table,[key],9,CancellationToken.None)); await second.InsertSourceAsync(table,[key],1,CancellationToken.None); Assert.NotEqual(first.SourceTableName(table),second.SourceTableName(table)); Assert.Equal(1,await first.GenerationAsync(table,key,CancellationToken.None)); Assert.Equal(2,await first.KeyColumnCountInUniqueIndexAsync(table,CancellationToken.None)); }
}
```

- [ ] **Step 2: Run it and confirm staging is absent.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerStagingTablesTests"`

Expected: compilation fails with `The type or namespace name 'SqlServerStagingTables' could not be found`.

- [ ] **Step 3: Implement native typed source/input/target stages and direct `SqlBulkCopy`.**

```csharp
using System.Data; using DataPitcher.Core.Identity; using DataPitcher.Core.Schema; using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer;
public sealed class SqlServerStagingTables : IAsyncDisposable
{
    private readonly string _source,_target; private readonly SqlServerSchemaSnapshot _schema; private readonly IReadOnlyDictionary<TableDefinition,StableKeySelection> _keys; private readonly string _plan=Guid.NewGuid().ToString("N"); private readonly Dictionary<TableDefinition,int> _ordinals=[]; private int _next;
    public SqlServerStagingTables(string source,string target,SqlServerSchemaSnapshot schema,IReadOnlyDictionary<TableDefinition,StableKeySelection> keys) { _source=source;_target=target;_schema=schema;_keys=keys; }
    public string SourceConnectionString=>_source; public string TargetConnectionString=>_target;
    public string SourceTableName(TableDefinition t)=>$"keys_{_plan}_{Ordinal(t):x8}"; public string InputTableName(TableDefinition t)=>$"input_{_plan}_{Ordinal(t):x8}"; public string TargetTableName(TableDefinition t)=>$"target_{_plan}_{Ordinal(t):x8}";
    public async Task<IReadOnlyCollection<StableKey>> InsertSourceAsync(TableDefinition t,IReadOnlyCollection<StableKey> keys,int generation,CancellationToken ct) { await EnsureAsync(_source,SourceTableName(t),t,ct);await ReplaceAsync(_source,InputTableName(t),t,keys,ct);return await InsertNewAsync(t,generation,ct); }
    public Task ReplaceSourceCandidatesAsync(TableDefinition t,IReadOnlyCollection<StableKey> keys,CancellationToken ct)=>ReplaceAsync(_source,InputTableName(t),t,keys,ct); public Task ReplaceTargetCandidatesAsync(TableDefinition t,IReadOnlyCollection<StableKey> keys,CancellationToken ct)=>ReplaceAsync(_target,TargetTableName(t),t,keys,ct);
    public async Task<int> GenerationAsync(TableDefinition t,StableKey key,CancellationToken ct) { var columns=Columns(t);await using var c=new SqlConnection(_source);await c.OpenAsync(ct);await using var q=new SqlCommand($"SELECT [__generation] FROM {Qualified(SourceTableName(t))} WHERE "+string.Join(" AND ",columns.Select((_,i)=>$"[k{i}]=@p{i}")),c);foreach(var pair in columns.Select((x,i)=>(x,i)))q.Parameters.AddWithValue($"@p{pair.i}",key.Components.Single(k=>k.Column==pair.x).Value!);return Convert.ToInt32(await q.ExecuteScalarAsync(ct)); }
    public async Task<int> KeyColumnCountInUniqueIndexAsync(TableDefinition t,CancellationToken ct) { await using var c=new SqlConnection(_source);await c.OpenAsync(ct);await using var q=new SqlCommand("SELECT COUNT(*) FROM sys.index_columns WHERE object_id=OBJECT_ID(@table) AND index_id=(SELECT index_id FROM sys.indexes WHERE object_id=OBJECT_ID(@table) AND name=@index)",c);q.Parameters.AddWithValue("@table","dbo."+SourceTableName(t));q.Parameters.AddWithValue("@index","UX_"+SourceTableName(t));return Convert.ToInt32(await q.ExecuteScalarAsync(ct)); }
    public async ValueTask DisposeAsync(){foreach(var t in _ordinals.Keys){await DropAsync(_source,SourceTableName(t));await DropAsync(_source,InputTableName(t));await DropAsync(_target,TargetTableName(t));}}
    private int Ordinal(TableDefinition t)=>_ordinals.TryGetValue(t,out var i)?i:_ordinals[t]=_next++; private IReadOnlyList<string> Columns(TableDefinition t)=>_keys[t].Constraint!.Columns; public static string Qualified(string name)=>SqlServerIdentifier.Qualified("dbo",name);
    private async Task ReplaceAsync(string cs,string name,TableDefinition t,IReadOnlyCollection<StableKey> keys,CancellationToken ct){await EnsureAsync(cs,name,t,ct);await ExecuteAsync(cs,"TRUNCATE TABLE "+Qualified(name),ct);await BulkCopyAsync(cs,name,t,keys.Distinct().ToArray(),ct);}
    private async Task EnsureAsync(string cs,string name,TableDefinition t,CancellationToken ct){var metadata=_schema.Table(t.Name);var declarations=string.Join(", ",Columns(t).Select((column,i)=>$"[k{i}] {metadata.Column(column).StoreType} NOT NULL"));var index="UX_"+name;await ExecuteAsync(cs,$"IF OBJECT_ID(N'{Qualified(name)}',N'U') IS NULL BEGIN CREATE TABLE {Qualified(name)} ({declarations}, [__generation] int NOT NULL); CREATE UNIQUE INDEX {SqlServerIdentifier.Quote(index)} ON {Qualified(name)} ({string.Join(", ",Columns(t).Select((_,i)=>$"[k{i}]"))}); END",ct);}
    private async Task BulkCopyAsync(string cs,string name,TableDefinition t,IReadOnlyCollection<StableKey> keys,CancellationToken ct){var data=new DataTable();var columns=Columns(t);var metadata=_schema.Table(t.Name);foreach(var pair in columns.Select((column,i)=>(column,i)))data.Columns.Add($"k{pair.i}",metadata.Column(pair.column).ClrType);data.Columns.Add("__generation",typeof(int));foreach(var key in keys){var row=data.NewRow();foreach(var pair in columns.Select((column,i)=>(column,i)))row[$"k{pair.i}"]=key.Components.Single(x=>x.Column==pair.column).Value!;row["__generation"]=0;data.Rows.Add(row);}await using var c=new SqlConnection(cs);await c.OpenAsync(ct);using var bulk=new SqlBulkCopy(c,SqlBulkCopyOptions.Default,null){DestinationTableName=Qualified(name),EnableStreaming=true};foreach(DataColumn column in data.Columns)bulk.ColumnMappings.Add(column.ColumnName,column.ColumnName);await bulk.WriteToServerAsync(data,ct);}
    private async Task<IReadOnlyCollection<StableKey>> InsertNewAsync(TableDefinition t,int generation,CancellationToken ct){var columns=Columns(t);var names=string.Join(", ",columns.Select((_,i)=>$"[k{i}]"));var join=string.Join(" AND ",columns.Select((_,i)=>$"s.[k{i}]=i.[k{i}]"));var sql=$"INSERT {Qualified(SourceTableName(t))} ({names},[__generation]) OUTPUT {string.Join(", ",columns.Select((_,i)=>$"INSERTED.[k{i}]"))} SELECT {names},@generation FROM {Qualified(InputTableName(t))} i WHERE NOT EXISTS (SELECT 1 FROM {Qualified(SourceTableName(t))} s WHERE {join})";await using var c=new SqlConnection(_source);await c.OpenAsync(ct);await using var q=new SqlCommand(sql,c);q.Parameters.AddWithValue("@generation",generation);await using var r=await q.ExecuteReaderAsync(ct);var result=new List<StableKey>();while(await r.ReadAsync(ct))result.Add(new(columns.Select((column,i)=>new KeyComponent(column,r.GetValue(i)))));return result;}
    private static async Task ExecuteAsync(string cs,string sql,CancellationToken ct){await using var c=new SqlConnection(cs);await c.OpenAsync(ct);await using var q=new SqlCommand(sql,c);await q.ExecuteNonQueryAsync(ct);} private static Task DropAsync(string cs,string name)=>ExecuteAsync(cs,"DROP TABLE IF EXISTS "+Qualified(name),CancellationToken.None);
}
```

`StoreType` is catalog-native `int` or sized `nvarchar(n)`, never JSON. The index covers every stable-key component. Candidate replacement truncates then directly bulk-copies; `NOT EXISTS` has no update path, preserving the earlier generation. Do not add transfer transactions, identity handling, or fallback writers.

- [ ] **Step 4: Run the staging test and confirm it passes.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerStagingTablesTests"`

Expected: one passing test; both plans can stage the same composite key without collision, the unique index has both declaration-order key columns, native CLR values arrive through `SqlBulkCopy`, and generation remains 1.

- [ ] **Step 5: Commit typed native staging.**

Run: `git add src/DataPitcher.Providers.SqlServer/SqlServerStagingTables.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerStagingTablesTests.cs && git commit -m "feat: stage typed sql server closure keys"`

### Task 6: Implement the four SQL Server closure-store operations

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerClosureStore.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerClosureStoreTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerClosureStoreTests.cs`

- [ ] **Step 1: Write the failing positional expansion and batched-probe test.**

```csharp
using DataPitcher.Core.Closure; using DataPitcher.Core.Identity; using DataPitcher.Core.Schema; using DataPitcher.Providers.SqlServer; using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[Collection("SqlServer closure")]
public sealed class SqlServerClosureStoreTests(SqlServerClosureFixture fixture)
{
    [Fact] public async Task ExpandAsync_UsesForeignKeyPositionAndProbeReportsTargetTrust()
    { await using var scope=await fixture.CreateScopeAsync();await scope.ExecuteAsync("INSERT dbo.composite_parent VALUES (7,8); INSERT dbo.composite_child VALUES (1,8,7);");var source=await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo",CancellationToken.None);var target=await new SqlServerCatalogReader(scope.TargetConnectionString).ReadAsync("dbo",CancellationToken.None);var child=source.Table("composite_child").Definition;var parent=source.Table("composite_parent").Definition;var p=source.Table("untrusted_parents").Definition;var fk=new ClosureRelationship(source.ForeignKey("FK_composite_child_parent"));var keys=new Dictionary<TableDefinition,StableKeySelection>{{child,StableKeySelector.Select(child,null)},{parent,StableKeySelector.Select(parent,null)},{p,StableKeySelector.Select(p,null)}};await using var store=new SqlServerClosureStore(scope.SourceConnectionString,scope.TargetConnectionString,source,target,keys);Assert.Contains(await store.ExpandAsync(fk,[new StableKey([new("id",1)])],CancellationToken.None),key=>key==new StableKey([new("left_value",7),new("right_value",8)]));var g=source.Table("untrusted_grandparents").Definition;var probe=await store.ProbeTargetAsync(p,[new ClosureRelationship(source.ForeignKey("FK_P_G"))],[new StableKey([new("id",2)])],CancellationToken.None);Assert.True(probe[new StableKey([new("id",2)])].Exists);Assert.False(probe.Values.Single().Constraints.Values.Single().IsTrusted); }
}
```

- [ ] **Step 2: Run it and confirm the store is absent.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerClosureStoreTests"`

Expected: compilation fails with `The type or namespace name 'SqlServerClosureStore' could not be found`.

- [ ] **Step 3: Implement the existing interface exactly, using staged set joins.**

```csharp
using DataPitcher.Core.Closure; using DataPitcher.Core.Identity; using DataPitcher.Core.Schema; using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer;
public sealed class SqlServerClosureStore : IClosureStore, IAsyncDisposable
{
    private readonly SqlServerStagingTables _stages; private readonly SqlServerSchemaSnapshot _source,_target; private readonly IReadOnlyDictionary<TableDefinition,StableKeySelection> _keys;
    public SqlServerClosureStore(string source,string target,SqlServerSchemaSnapshot sourceSchema,SqlServerSchemaSnapshot targetSchema,IReadOnlyDictionary<TableDefinition,StableKeySelection> keys) { _stages=new(source,target,sourceSchema,keys);_source=sourceSchema;_target=targetSchema;_keys=keys; }
    public Task<IReadOnlyCollection<StableKey>> SeedRootKeysAsync(TableDefinition t,IReadOnlyCollection<StableKey> keys,CancellationToken ct)=>_stages.InsertSourceAsync(t,keys,0,ct); public Task<IReadOnlyCollection<StableKey>> InsertNewKeysAsync(TableDefinition t,IReadOnlyCollection<StableKey> keys,int generation,CancellationToken ct)=>_stages.InsertSourceAsync(t,keys,generation,ct);
    public async Task<IReadOnlyDictionary<StableKey,TargetProbe>> ProbeTargetAsync(TableDefinition t,IReadOnlyCollection<ClosureRelationship> outgoing,IReadOnlyCollection<StableKey> keys,CancellationToken ct) { await _stages.ReplaceTargetCandidatesAsync(t,keys,ct);var columns=KeyColumns(t);var sql=$"/* DataPitcher.ProbeTarget */ SELECT {string.Join(", ",columns.Select((_,i)=>$"s.[k{i}]"))}, CASE WHEN t.{SqlServerIdentifier.Quote(columns[0])} IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END FROM {SqlServerStagingTables.Qualified(_stages.TargetTableName(t))} s LEFT JOIN {Qualified(t)} t ON {string.Join(" AND ",columns.Select((c,i)=>$"s.[k{i}]=t.{SqlServerIdentifier.Quote(c)}"))}";var states=outgoing.ToDictionary(r=>r,TargetState);var result=new Dictionary<StableKey,TargetProbe>();await using var c=new SqlConnection(_stages.TargetConnectionString);await c.OpenAsync(ct);await using var q=new SqlCommand(sql,c);await using var rows=await q.ExecuteReaderAsync(ct);while(await rows.ReadAsync(ct))result[ReadKey(rows,t)]=new TargetProbe(rows.GetBoolean(columns.Count),states);return result; }
    public async Task<IReadOnlyCollection<StableKey>> ExpandAsync(ClosureRelationship r,IReadOnlyCollection<StableKey> fromKeys,CancellationToken ct) { await _stages.ReplaceSourceCandidatesAsync(r.FromTable,fromKeys,ct);var fromKey=KeyColumns(r.FromTable);var toKey=KeyColumns(r.ToTable);var sql=$"SELECT DISTINCT {string.Join(", ",toKey.Select(c=>$"t.{SqlServerIdentifier.Quote(c)}"))} FROM {SqlServerStagingTables.Qualified(_stages.InputTableName(r.FromTable))} s JOIN {Qualified(r.FromTable)} f ON {string.Join(" AND ",fromKey.Select((c,i)=>$"s.[k{i}]=f.{SqlServerIdentifier.Quote(c)}"))} JOIN {Qualified(r.ToTable)} t ON {string.Join(" AND ",r.FromColumns.Zip(r.ToColumns).Select(p=>$"f.{SqlServerIdentifier.Quote(p.First)}=t.{SqlServerIdentifier.Quote(p.Second)}"))} WHERE {string.Join(" AND ",r.FromColumns.Select(c=>$"f.{SqlServerIdentifier.Quote(c)} IS NOT NULL"))}";await using var c=new SqlConnection(_stages.SourceConnectionString);await c.OpenAsync(ct);await using var q=new SqlCommand(sql,c);await using var rows=await q.ExecuteReaderAsync(ct);var result=new List<StableKey>();while(await rows.ReadAsync(ct))result.Add(ReadKey(rows,r.ToTable));return result; }
    public ValueTask DisposeAsync()=>_stages.DisposeAsync(); private IReadOnlyList<string> KeyColumns(TableDefinition t)=>_keys[t].Constraint!.Columns; private static string Qualified(TableDefinition t)=>SqlServerIdentifier.Qualified(t.Schema,t.Name);
    private TargetConstraintState TargetState(ClosureRelationship r) { if(r.ForeignKey is not { } source) return new(r.Name,false,false,false);var target=_target.ForeignKeys.SingleOrDefault(f=>f.ChildTable==source.ChildTable&&f.ParentTable==source.ParentTable&&f.ChildColumns.SequenceEqual(source.ChildColumns)&&f.ParentColumns.SequenceEqual(source.ParentColumns));return target is null?new(r.Name,false,false,false):new(target.Name,true,target.IsEnforced,target.IsTrusted); }
    private StableKey ReadKey(SqlDataReader rows,TableDefinition t) { var columns=KeyColumns(t);var metadata=_source.Table(t.Name);var components=columns.Select((c,i)=>new KeyComponent(c,rows.GetValue(i))).ToArray();if(components.Where((x,i)=>x.Value.GetType()!=metadata.Column(columns[i]).ClrType).Any())throw new InvalidOperationException("Stable-key CLR type does not match catalog metadata.");return new(components); }
}
```

`ExpandAsync` uses `ClosureRelationship.FromColumns` and `ToColumns` directly, so constraint-derived, inbound, and manual relationships use the same positional mapping. `ProbeTargetAsync` bulk-moves candidates to the target, then emits one tagged left join—not per-key queries or `IN`. It maps target metadata by child/parent table and ordered column pairs, returning absent, disabled, and untrusted states as Core expects.

- [ ] **Step 4: Run the direct store test and confirm it passes.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerClosureStoreTests"`

Expected: one passing test; `(child_right, child_left)` maps positionally to `(left_value, right_value)`, the existing target parent is found, and its enabled/untrusted constraint is reported untrusted.

- [ ] **Step 5: Commit the real store.**

Run: `git add src/DataPitcher.Providers.SqlServer/SqlServerClosureStore.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerClosureStoreTests.cs && git commit -m "feat: add sql server closure store"`

### Task 7: Re-run the thirty-one closure behaviours without changing assertions

**Files:**
- Create: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerDependencyClosureTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerDependencyClosureTests.cs`

- [ ] **Step 1: Write the real-engine suite before its scenario helper.**

```csharp
// Copy exactly the 31 [Fact] methods at DependencyClosureTests.cs:5-35.
// Retain every Assert.* expression byte-for-byte; only replace fake setup with SQL Server rows.
[Fact] public async Task Closure_WhenParentTargetConstraintIsUntrusted_TransfersParentAndNamesTheConstraint()
{ await using var s=await SqlServerClosureScenario.CreateAsync(_fixture); var (c,p,g,cp,pg)=await s.UntrustedAsync(); var r=await s.RunAsync([cp,pg],s.Root(c,1)); Assert.True(r.Contains(p,s.Key("id",2))); Assert.True(r.Contains(g,s.Key("id",3))); Assert.Contains(new TargetConstraintWarning($"Target_{pg.Name}"),r.Warnings); }
```

The file contains all thirty-one facts through `DependencyClosureTests.cs:35`: child/parent, inbound, nullable/composite/unique FK, dual/manual/orphan/shared paths, root policies, target states, disagreement, disabled relationship, cycles, generations, blocked/no-selection/unique cases, and diamonds. Do not copy later facts. Assertion expressions—not merely meaning—are unchanged.

- [ ] **Step 2: Run it and confirm the scenario helper is missing.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerDependencyClosureTests"`

Expected: compilation fails with `The name 'SqlServerClosureScenario' does not exist in the current context`.

- [ ] **Step 3: Add the SQL Server scenario translation and no assertion edits.**

```csharp
internal sealed class SqlServerClosureScenario : IAsyncDisposable
{
    private readonly SqlServerClosureScope _scope; private SqlServerClosureScenario(SqlServerClosureScope scope) => _scope=scope;
    public static async Task<SqlServerClosureScenario> CreateAsync(SqlServerClosureFixture fixture) => new(await fixture.CreateScopeAsync());
    public string TargetAdminConnectionString => _scope.TargetAdminConnectionString;
    public async Task<(TableDefinition Child,TableDefinition Parent,TableDefinition Grandparent,ClosureRelationship ChildParent,ClosureRelationship ParentGrandparent)> UntrustedAsync()
    { await BothAsync("CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.untrusted_parents(id))"); await _scope.ExecuteAsync("INSERT dbo.untrusted_grandparents VALUES (3); INSERT dbo.untrusted_parents VALUES (2,3); INSERT dbo.c VALUES (1,2);"); var (source,target)=await CatalogsAsync(); var c=source.Table("c").Definition;var p=source.Table("untrusted_parents").Definition;var g=source.Table("untrusted_grandparents").Definition;return(c,p,g,new(source.ForeignKeys.Single(f=>f.ChildTable==c&&f.ParentTable==p)),new(source.ForeignKey("FK_P_G"))); }
    public ClosureRoot Root(TableDefinition table,int id,RootConflictPolicy policy=RootConflictPolicy.FailOnConflict)=>new(table,[Key("id",id)],policy); public StableKey Key(string column,object value)=>new([new KeyComponent(column,value)]);
    public async Task<ClosureResult> RunAsync(IReadOnlyCollection<ClosureRelationship> relationships,params ClosureRoot[] roots) { var (source,target)=await CatalogsAsync(); var selections=roots.Select(r=>r.Table).Concat(relationships.SelectMany(r=>new[]{r.FromTable,r.ToTable})).Distinct().ToDictionary(t=>t,t=>StableKeySelector.Select(t,null)); await using var store=new SqlServerClosureStore(_scope.SourceConnectionString,_scope.TargetConnectionString,source,target,selections);return await new DependencyClosure(store).ComputeAsync(new ClosureRequest(roots,relationships,selections),CancellationToken.None); }
    private async Task<(SqlServerSchemaSnapshot Source,SqlServerSchemaSnapshot Target)> CatalogsAsync()=> (await new SqlServerCatalogReader(_scope.SourceConnectionString).ReadAsync("dbo",CancellationToken.None),await new SqlServerCatalogReader(_scope.TargetConnectionString).ReadAsync("dbo",CancellationToken.None));
    private async Task BothAsync(string sql){await _scope.ExecuteAsync(sql);await _scope.ExecuteTargetAsync(sql);} public ValueTask DisposeAsync()=>_scope.DisposeAsync();
}
```

For each remaining fact, create identical tables in both databases before source-only rows. Translate `Link`/`AddRow` to source `INSERT`, `MarkTarget` to target `INSERT`, and manual links to `ClosureRelationship.Manual` with exact ordered columns. Create orphans by inserting before `WITH NOCHECK ADD CONSTRAINT`; disable target enforcement with `ALTER TABLE dbo.p NOCHECK CONSTRAINT [FK_p_g]`; disable only source metadata for the disagreement case. Retain seeded target `Target_FK_P_G` and `(2,3)` with its grandparent absent. Insert the two-table cycle as `cycle_a(1,NULL)`, `cycle_b(2,1)`, then update `cycle_a.b_id=2`. In the satisfied-path diamond target `a(2,NULL)` under a trusted nullable FK. These translations preserve every Slice 1 assertion.

- [ ] **Step 4: Run the point-of-slice behavioural suite and confirm it passes unchanged.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerDependencyClosureTests"`

Expected: `Passed: 31. Failed: 0.` Assertions run against separate emulated source and target containers. If one cannot hold, stop and report the assertion and real-store finding; never edit, weaken, skip, or replace it. PostgreSQL’s equivalent re-run exposed a model defect concealed by the fake, so failure is evidence—not permission to force green.

- [ ] **Step 5: Commit the behavioural re-run.**

Run: `git add tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerDependencyClosureTests.cs && git commit -m "test: rerun closure contract against sql server"`

### Task 8: Prove target probes are batched on the SQL Server wire

**Files:**
- Create: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerProbeBatchingTests.cs`
- Modify: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerDependencyClosureTests.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerWireCommandRecorder.cs`
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerProbeBatchingTests.cs`

- [ ] **Step 1: Write the failing server-observed batching test.**

```csharp
using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[Collection("SqlServer closure")]
public sealed class SqlServerProbeBatchingTests(SqlServerClosureFixture fixture)
{
    [Fact] public async Task ComputeAsync_SendsOneTargetProbePerFrontierTableNotPerKey()
    { await using var s=await SqlServerClosureScenario.CreateAsync(fixture); await using var wire=await SqlServerWireCommandRecorder.StartAsync(s.TargetAdminConnectionString,"DataPitcher.ProbeTarget"); await s.CreateBatchChainAsync(40); await s.RunBatchAsync(); Assert.Equal(1,await wire.Count("DataPitcher.ProbeTarget", "batch_child")); Assert.Equal(1,await wire.Count("DataPitcher.ProbeTarget", "batch_parent")); Assert.Equal(2,await wire.Count("DataPitcher.ProbeTarget")); Assert.False(await wire.AnyContainsLargeInListAsync(10)); }
}
```

Add `CreateBatchChainAsync`, `RunBatchAsync`, `Count(tag, table)`, and `AnyContainsLargeInListAsync(threshold)` to the existing APIs. The setup creates parent/child tables in both databases, inserts forty source pairs, and computes from forty child roots. Filters inspect server-captured `sql_text`, not store calls.

- [ ] **Step 2: Run it and confirm the required batching APIs are absent.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerProbeBatchingTests"`

Expected: compilation fails with missing `CreateBatchChainAsync` and `RunBatchAsync` members.

- [ ] **Step 3: Implement only the test helper members and server-text filters.**

```csharp
// SqlServerWireCommandRecorder additions
public async Task<int> Count(string tag,string? table) => (await SqlTextsAsync()).Count(x=>x.Contains(tag,StringComparison.Ordinal)&&(table is null||x.Contains(SqlServerIdentifier.Quote(table),StringComparison.Ordinal)));
public async Task<bool> AnyContainsLargeInListAsync(int threshold) => (await SqlTextsAsync()).Any(x=>x.Split(" IN (",StringSplitOptions.None).Skip(1).Any(p=>p.Split(')')[0].Split(',').Length>threshold));
// SqlServerClosureScenario additions
public async Task CreateBatchChainAsync(int count) { await BothAsync("CREATE TABLE dbo.batch_parent (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.batch_child (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.batch_parent(id))");var p=string.Join(",",Enumerable.Range(1,count).Select(i=>$"({i})"));await _scope.ExecuteAsync($"INSERT dbo.batch_parent VALUES {p}; INSERT dbo.batch_child VALUES {string.Join(",",Enumerable.Range(1,count).Select(i=>$"({i},{i})"))};"); }
public async Task<ClosureResult> RunBatchAsync() { var (source,target)=await CatalogsAsync();var child=source.Table("batch_child").Definition;var parent=source.Table("batch_parent").Definition;var relation=new ClosureRelationship(source.ForeignKeys.Single(f=>f.ChildTable==child&&f.ParentTable==parent));var selections=new Dictionary<TableDefinition,StableKeySelection>{{child,StableKeySelector.Select(child,null)},{parent,StableKeySelector.Select(parent,null)}};var roots=Enumerable.Range(1,40).Select(i=>new ClosureRoot(child,[Key("id",i)],RootConflictPolicy.FailOnConflict));await using var store=new SqlServerClosureStore(_scope.SourceConnectionString,_scope.TargetConnectionString,source,target,selections);return await new DependencyClosure(store).ComputeAsync(new ClosureRequest(roots,[relation],selections),CancellationToken.None); }
```

The tag is already emitted by Task 6. These helpers inspect only Task 3’s captured server XML; production code does not change.

- [ ] **Step 4: Run the SQL Server lane and the aggregate coverage gate.**

Run: `./scripts/test-sqlserver.sh && ./scripts/test-all.sh`

Expected: SQL Server integration tests pass under amd64 emulation with two containers only; the server-side Extended Events test reports one actual target probe RPC for `batch_child`, one for `batch_parent`, two total, and no large `IN` list. The aggregate command exits 0 and prints `Merged coverage: line=100% branch=100% method=100%` after ReportGenerator merges every solution report.

- [ ] **Step 5: Commit the wire-level proof.**

Run: `git add tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerProbeBatchingTests.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerDependencyClosureTests.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerWireCommandRecorder.cs && git commit -m "test: prove sql server target probe batching"`

## Self-Review

- [ ] **Covered:** all required fixture shapes and enabled/untrusted FK, three-query ordered catalog metadata, CLR/nullability, live bracket safety, typed `SqlBulkCopy` staging, immutable generations, set expansion, target trust/enforcement, unchanged 31 behaviours, server command counting, the lane, warnings-as-errors, and the merged 100% ReportGenerator gate.
- [ ] **Deferred:** transfer execution/writing/verification, transfer identity/transactions, cycle execution, LINQ to DB bulk paths, broad type support, staging TTLs, and CI scheduling.
- [ ] **Consistency checked:** `SqlServerSchemaSnapshot`, `SqlServerCatalogReader`, `SqlServerIdentifier`, `SqlServerStagingTables`, `SqlServerClosureStore`, `SqlServerClosureFixture`, `SqlServerClosureScope`, `SqlServerClosureScenario`, and `SqlServerWireCommandRecorder` are defined before downstream use. `SeedRootKeysAsync`, `ProbeTargetAsync`, `ExpandAsync`, and `InsertNewKeysAsync`; package pins; image; names; and trust assertions match throughout.
