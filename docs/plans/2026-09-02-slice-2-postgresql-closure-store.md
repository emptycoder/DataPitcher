# DataPitcher Slice 2: PostgreSQL-Backed Closure Store Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a PostgreSQL-backed `IClosureStore` so Slice 1’s thirty-one closure behavioural tests re-run unchanged in assertion against real source and target PostgreSQL databases.

**Architecture:** Add a narrow Npgsql provider project: catalog metadata supplies ordered schema facts, a per-store staging manager owns typed key tables, and `PostgreSqlClosureStore` implements the existing four-method Core seam. Source expansion stays set-based in the source container; candidate stable keys cross to target-owned staging through Npgsql binary COPY before one target-side probe per frontier table and generation. The Core `DependencyClosure` remains unmodified.

**Tech Stack:** .NET 10/C# latest; Npgsql **10.0.3**; Testcontainers.PostgreSql **4.14.0**; `postgres:17-alpine`; xUnit; Coverlet; PostgreSQL catalogs; Npgsql binary `COPY FROM STDIN`.

---

## File Structure

- `DataPitcher.sln` — add the PostgreSQL provider project.
- `src/DataPitcher.Providers.PostgreSql/DataPitcher.Providers.PostgreSql.csproj` — Core reference and pinned Npgsql 10.0.3.
- `src/DataPitcher.Providers.PostgreSql/PostgreSqlCatalogReader.cs` — PostgreSQL catalog snapshot, native type metadata, and manual-relationship mapping.
- `src/DataPitcher.Providers.PostgreSql/PostgreSqlIdentifier.cs` — PostgreSQL identifier quoting.
- `src/DataPitcher.Providers.PostgreSql/PostgreSqlStagingTables.cs` — plan-scoped typed source/input/target key stages and binary COPY movement.
- `src/DataPitcher.Providers.PostgreSql/PostgreSqlClosureStore.cs` — real `IClosureStore` implementation and set-based probe/expand SQL.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj` — references Core and provider while retaining the pinned integration packages.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureFixture.cs` — two-container reusable fixture, unique schemas, deterministic DDL, and real-row scenario setup.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCatalogReaderTests.cs` — catalog declaration-order, nullability/type, unique-key, FK, and validation tests.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlIdentifierTests.cs` — quoted identifier safety tests.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlStagingTablesTests.cs` — typed stages, immutable generations, cleanup, and concurrent-plan isolation tests.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureStoreTests.cs` — direct real-store expansion and stable-key materialization tests.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlDependencyClosureTests.cs` — real-engine copies of Slice 1’s first 31 behavioural fixtures with identical assertions.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCommandRecorder.cs` — Npgsql execution-log recorder used to count emitted target probe commands.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlProbeBatchingTests.cs` — database-command batching proof.
- `scripts/test-unit.sh` — Docker-free unit plus architecture tests; collects and prints coverage for information only, no threshold gate.
- `scripts/test-postgres.sh` — PostgreSQL integration tests, no coverage gate.
- `scripts/test-all.sh` — the sole 100% line/branch/method coverage gate, run as a single pass over the whole solution.
- `.github/workflows/ci.yml` — separate fast and Docker-backed jobs.
- `docs/plans/2026-09-02-slice-2-postgresql-closure-store.md` — this plan.

## Scope and Deferrals

This deliberately merges roadmap Slices 4 and 5: catalog discovery and the PostgreSQL closure store are inseparable evidence for the `IClosureStore` seam. It deliberately defers full capability detection, dialect breadth, staging TTL cleanup, and SQL Server. Building provider surface area before the seam is proven is building ahead of evidence. PostgreSQL staging is dropped by the plan store’s async disposal; durable cleanup policies are not added here.

ADR 0005 forbids LINQ to DB `BulkCopy` in this write path. Do not add LINQ to DB to the provider project and do not substitute multi-row or row-by-row inserts: every bulk key movement below uses Npgsql’s asynchronous binary importer directly.

### Task 1: Fast and slow test split

**Why the coverage gate is aggregate-only:** Coverage is a property of the whole set of handwritten production code, not of any one test lane. `scripts/test-postgres.sh` runs only the integration project; today that project references no production code at all, so a gate on that lane alone instruments nothing and fails by construction (0/0 is reported as 0%, not 100%). Once provider code exists (Tasks 3–8), the unit lane still never exercises it — it requires a real database — and the integration lane never exercises the domain that the unit lane covers. No single lane can ever satisfy a 100% gate once both kinds of production code exist. The gate must therefore live only in `scripts/test-all.sh`, which runs the whole solution in one pass across every emitted report before computing the percentage.

The per-report reports are not disjoint by assembly. An integration test project necessarily references the production code it exercises, so once the PostgreSQL provider project exists, `DataPitcher.Core` is instrumented in both the unit report (fully, via `DataPitcher.UnitTests`) and the integration report (partially, via the provider's transitive reference), because `DataPitcher.Providers.PostgreSql.IntegrationTests` links `DataPitcher.Providers.PostgreSql`, which links `DataPitcher.Core`. Summing raw sequence/branch/method counts across reports double-counts that shared assembly and drives the aggregate below 100% even when every line is covered by some test. The correct operation across multiple reports of the same solution is a per-assembly, per-method, per-line UNION, not a sum: a line counts as covered if any report says so. `scripts/test-all.sh` therefore merges every emitted `coverage.opencover.xml` with ReportGenerator before computing the percentage, and gates on the merged figures. Do not add a per-lane coverage gate; if one is proposed later, point back to this note.

**Files:**
- Create: `scripts/test-unit.sh`, `scripts/test-postgres.sh`
- Modify: `scripts/test-all.sh`, `.github/workflows/ci.yml`
- Test: `dotnet test DataPitcher.sln` (aggregate run via `scripts/test-all.sh`)

- [ ] **Step 1: Write the failing missing-script check.**

Run: `DOCKER_HOST=unix:///definitely-not-a-docker-socket ./scripts/test-unit.sh; echo "exit=$?"`

Expected: exit 1 (not 127 — bash reports exit 1, not 127, for a missing command when it is invoked with a leading environment-variable assignment) with `./scripts/test-unit.sh: No such file or directory`.

- [ ] **Step 2: Create the scripts and split CI.**

```bash
#!/usr/bin/env bash
# scripts/test-unit.sh — fast lane: unit + architecture tests only. Collects and
# prints coverage for information; does NOT gate on it (see the aggregate-only
# note above — this lane never sees provider code).
set -euo pipefail
rm -rf artifacts/unit-test-results
dotnet build tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj
dotnet build tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj
dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --no-build "$@" --collect:"XPlat Code Coverage" --results-directory artifacts/unit-test-results -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
dotnet test tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj --no-build
report="$(find artifacts/unit-test-results -name coverage.opencover.xml -print -quit)"
if [ -n "$report" ]; then
  read -r sequence branch visited total < <(xmllint --xpath 'concat(/CoverageSession/Summary/@sequenceCoverage," ",/CoverageSession/Summary/@branchCoverage," ",/CoverageSession/Summary/@visitedMethods," ",/CoverageSession/Summary/@numMethods)' "$report")
  awk -v s="$sequence" -v b="$branch" -v v="$visited" -v t="$total" 'BEGIN { m=t==0?100:v/t*100; printf "Unit lane coverage (informational, not gated): line=%s%% branch=%s%% method=%.2f%%\n",s,b,m }'
fi
```

```bash
#!/usr/bin/env bash
# scripts/test-postgres.sh — integration lane only, no coverage collection or gate.
set -euo pipefail
dotnet build tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj
dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --no-build "$@"
```

Replace `scripts/test-all.sh` with a script that builds and runs `dotnet test DataPitcher.sln` once with coverage collection, then sums `numSequencePoints`/`visitedSequencePoints`, `numBranchPoints`/`visitedBranchPoints`, and `numMethods`/`visitedMethods` across every `coverage.opencover.xml` the solution-wide run emits (one per test project), and gates at 100% on the summed totals — not on any single report's own percentage. Summing raw counts, rather than averaging each report's own percentage, is what makes an all-zero report (an instrumented-nothing lane) a harmless no-op addition instead of a spurious 0%. Replace the workflow's unit job step label to drop "coverage gate" language (the unit lane no longer gates); keep it running `./scripts/test-unit.sh` after `libxml2-utils`, and keep the separate `postgres` job running `./scripts/test-postgres.sh` labelled `Run PostgreSQL container integration tests`.

- [ ] **Step 3: Run `scripts/test-all.sh` and confirm the aggregate gate passes.**

Run: `./scripts/test-all.sh`

Expected: exit 0; all three test assemblies run; reported aggregate coverage is line=100%, branch=100%, method=100%.

- [ ] **Step 4: Commit the fast/slow split.**

Run: `git add scripts/test-unit.sh scripts/test-postgres.sh scripts/test-all.sh .github/workflows/ci.yml && git commit -m "test: split unit and postgres lanes"`

### Task 2: A seeded PostgreSQL test database

**Files:**
- Create: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureFixture.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureFixture.cs`

- [ ] **Step 1: Write the failing fixture test.**

```csharp
[Fact]
public async Task Scope_WhenCreated_ContainsDeterministicSchemaAndUnvalidatedForeignKey()
{
    await using var scope = await _fixture.CreateScopeAsync();
    Assert.Equal(16, await scope.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema();"));
    Assert.False(await scope.ScalarAsync<bool>("SELECT convalidated FROM pg_constraint WHERE conname = 'Target_FK_P_G';"));
}
```

- [ ] **Step 2: Run it and confirm the missing-fixture failure.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlClosureFixtureTests"`

Expected: compilation fails with `The type or namespace name 'PostgreSqlClosureFixture' could not be found`.

- [ ] **Step 3: Add the two-container fixture and seeded schema.**

```csharp
public sealed class PostgreSqlClosureFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _source = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly PostgreSqlContainer _target = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public async Task InitializeAsync() { await _source.StartAsync(); await _target.StartAsync(); }
    public async Task DisposeAsync() { await _source.DisposeAsync(); await _target.DisposeAsync(); }
    public async Task<PostgreSqlClosureScope> CreateScopeAsync()
    {
        var schema = "dp_" + Guid.NewGuid().ToString("N");
        var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_source.GetConnectionString()) { SearchPath = schema }.ConnectionString);
        var target = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_target.GetConnectionString()) { SearchPath = schema }.ConnectionString);
        await PostgreSqlClosureScope.CreateAsync(source, schema, false);
        await PostgreSqlClosureScope.CreateAsync(target, schema, true);
        return new PostgreSqlClosureScope(schema, source, target);
    }
}

public sealed class PostgreSqlClosureScope(string schema, NpgsqlDataSource source, NpgsqlDataSource target) : IAsyncDisposable
{
    public string Schema { get; } = schema; public NpgsqlDataSource Source { get; } = source; public NpgsqlDataSource Target { get; } = target;
    public static async Task CreateAsync(NpgsqlDataSource dataSource, string schema, bool target)
    { await ExecuteOnAsync(dataSource, "CREATE SCHEMA " + Quote(schema)); foreach (var sql in SchemaSql(target)) await ExecuteOnAsync(dataSource, sql); }
    public Task ExecuteAsync(string sql) => ExecuteOnAsync(Source, sql);
    public Task ExecuteTargetAsync(string sql) => ExecuteOnAsync(Target, sql);
    public async Task<T> ScalarAsync<T>(string sql) { await using var command = Source.CreateCommand(sql); return (T)(await command.ExecuteScalarAsync())!; }
    public async ValueTask DisposeAsync() { await ExecuteOnAsync(Source, "DROP SCHEMA IF EXISTS " + Quote(Schema) + " CASCADE"); await ExecuteOnAsync(Target, "DROP SCHEMA IF EXISTS " + Quote(Schema) + " CASCADE"); await Source.DisposeAsync(); await Target.DisposeAsync(); }
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static async Task ExecuteOnAsync(NpgsqlDataSource dataSource, string sql) { await using var command = dataSource.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
    private static IEnumerable<string> SchemaSql(bool target) =>
    [
        "CREATE TABLE customers (customer_id integer PRIMARY KEY, external_code text NOT NULL UNIQUE)",
        "CREATE TABLE orders (order_id integer PRIMARY KEY, customer_id integer NOT NULL REFERENCES customers(customer_id))",
        "CREATE TABLE order_lines (line_id integer PRIMARY KEY, order_id integer NOT NULL REFERENCES orders(order_id))",
        "CREATE TABLE declared_key (physical_first integer NOT NULL, physical_second integer NOT NULL, CONSTRAINT pk_declared_key PRIMARY KEY (physical_second, physical_first))",
        "CREATE TABLE composite_parent (left_value integer NOT NULL, right_value integer NOT NULL, PRIMARY KEY (left_value, right_value))",
        "CREATE TABLE composite_child (id integer PRIMARY KEY, child_left integer NOT NULL, child_right integer NOT NULL, CONSTRAINT fk_composite_child_parent FOREIGN KEY (child_right, child_left) REFERENCES composite_parent(left_value, right_value))",
        "CREATE TABLE optional_orders (id integer PRIMARY KEY, customer_id integer NULL REFERENCES customers(customer_id))",
        "CREATE TABLE external_parents (id integer PRIMARY KEY, code text NOT NULL UNIQUE)", "CREATE TABLE external_children (id integer PRIMARY KEY, code text NOT NULL REFERENCES external_parents(code))",
        "CREATE TABLE employees (id integer PRIMARY KEY, manager_id integer NULL REFERENCES employees(id))", "CREATE TABLE cycle_a (id integer PRIMARY KEY, b_id integer NULL)",
        "CREATE TABLE cycle_b (id integer PRIMARY KEY, a_id integer NULL REFERENCES cycle_a(id))", "ALTER TABLE cycle_a ADD CONSTRAINT fk_cycle_a_b FOREIGN KEY (b_id) REFERENCES cycle_b(id)",
        "CREATE TABLE unique_only (code text NOT NULL UNIQUE)", "CREATE TABLE no_stable_key (value text NULL)",
        "CREATE TABLE untrusted_grandparents (id integer PRIMARY KEY)", "CREATE TABLE untrusted_parents (id integer PRIMARY KEY, grandparent_id integer NOT NULL)",
        target ? "INSERT INTO untrusted_parents VALUES (2,3); ALTER TABLE untrusted_parents ADD CONSTRAINT \"Target_FK_P_G\" FOREIGN KEY (grandparent_id) REFERENCES untrusted_grandparents(id) NOT VALID" : "ALTER TABLE untrusted_parents ADD CONSTRAINT \"FK_P_G\" FOREIGN KEY (grandparent_id) REFERENCES untrusted_grandparents(id)"
    ];
}
```

The `SchemaSql` statements cover customers/orders/order-lines, declaration-ordered composite keys, composite and nullable FKs, unique-key references, self-reference, a two-table cycle, unique-only and keyless tables, and the `NOT VALID` target FK. It inserts `(2,3)` before target `"Target_FK_P_G" ... NOT VALID`; source uses `"FK_P_G"`. The scope exposes source/target data sources, scalar and execute helpers, and disposal that drops only its generated schema.

```sql
ALTER TABLE untrusted_parents ADD CONSTRAINT "Target_FK_P_G" FOREIGN KEY (grandparent_id) REFERENCES untrusted_grandparents(id) NOT VALID;
```

- [ ] **Step 4: Run the fixture test and confirm it passes.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlClosureFixtureTests"`

Expected: one passing test; both PostgreSQL 17 containers start, the scope sees the specified schema, and `Target_FK_P_G` reports `convalidated = false`.

- [ ] **Step 5: Commit the fixture.**

Run: `git add tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureFixture.cs && git commit -m "test: add seeded postgres closure fixture"`

### Task 3: PostgreSQL catalog introspection

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/DataPitcher.Providers.PostgreSql.csproj`, `src/DataPitcher.Providers.PostgreSql/PostgreSqlCatalogReader.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCatalogReaderTests.cs`
- Modify: `DataPitcher.sln`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj`
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCatalogReaderTests.cs`

- [ ] **Step 1: Write the failing catalog tests.**

```csharp
[Fact]
public async Task ReadAsync_UsesConstraintDeclarationOrderAndReadsValidation()
{
    await using var scope = await _fixture.CreateScopeAsync();
    var source = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
    var target = await new PostgreSqlCatalogReader(scope.Target).ReadAsync(scope.Schema, CancellationToken.None);
    Assert.Equal(["physical_second", "physical_first"], source.Table("declared_key").Definition.PrimaryKey!.Columns);
    Assert.Equal(typeof(int), source.Table("optional_orders").Column("customer_id").ClrType);
    Assert.True(source.Table("optional_orders").Column("customer_id").IsNullable);
    Assert.Null(source.Table("unique_only").Definition.PrimaryKey);
    Assert.Equal(["code"], Assert.Single(source.Table("unique_only").Definition.UniqueConstraints).Columns);
    Assert.Equal(["child_right", "child_left"], source.ForeignKey("fk_composite_child_parent").ChildColumns);
    Assert.Equal(["left_value", "right_value"], source.ForeignKey("fk_composite_child_parent").ParentColumns);
    Assert.False(target.ForeignKey("Target_FK_P_G").IsTrusted);
}
```

- [ ] **Step 2: Run them and confirm the catalog reader is absent.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlCatalogReaderTests"`

Expected: compilation fails because `DataPitcher.Providers.PostgreSql` and `PostgreSqlCatalogReader` do not exist.

- [ ] **Step 3: Add the provider project and catalog implementation.**

```xml
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>DataPitcher.Providers.PostgreSql</RootNamespace></PropertyGroup><ItemGroup><ProjectReference Include="../DataPitcher.Core/DataPitcher.Core.csproj" /><PackageReference Include="Npgsql" Version="10.0.3" /></ItemGroup></Project>
```

```csharp
public sealed record PostgreSqlColumn(string Name, string StoreType, NpgsqlDbType NpgsqlDbType, Type ClrType, bool IsNullable);
public sealed record PostgreSqlTable(TableDefinition Definition, IReadOnlyList<PostgreSqlColumn> Columns)
{ public PostgreSqlColumn Column(string name) => Columns.Single(x => x.Name == name); }
public sealed class PostgreSqlSchemaSnapshot
{
    public PostgreSqlSchemaSnapshot(IEnumerable<PostgreSqlTable> tables, IEnumerable<ForeignKeyDefinition> foreignKeys) { Tables = tables.ToArray(); ForeignKeys = foreignKeys.ToArray(); }
    public IReadOnlyList<PostgreSqlTable> Tables { get; } public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; }
    public PostgreSqlTable Table(string name) => Tables.Single(x => x.Definition.Name == name);
    public ForeignKeyDefinition ForeignKey(string name) => ForeignKeys.Single(x => x.Name == name);
}
public sealed record PostgreSqlManualRelationship(ClosureRelationship Relationship, IReadOnlyList<string> FromColumns, IReadOnlyList<string> ToColumns);
public sealed class PostgreSqlCatalogReader(NpgsqlDataSource dataSource)
{
    public async Task<PostgreSqlSchemaSnapshot> ReadAsync(string schema, CancellationToken ct)
    {
        var columns = new Dictionary<string,List<PostgreSqlColumn>>(); await using (var command = Command("SELECT c.relname,a.attname,format_type(a.atttypid,a.atttypmod),t.typname,NOT a.attnotnull FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid JOIN pg_type t ON t.oid=a.atttypid WHERE n.nspname=@schema AND c.relkind IN ('r','p') AND a.attnum>0 AND NOT a.attisdropped ORDER BY c.relname,a.attnum", schema)) { await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) { var map = Map(reader.GetString(3)); columns.TryAdd(reader.GetString(0), []); columns[reader.GetString(0)].Add(new(reader.GetString(1),reader.GetString(2),map.DbType,map.ClrType,reader.GetBoolean(4))); } }
        var keys = new Dictionary<string,(UniqueConstraint? Primary,List<UniqueConstraint> Unique)>(); foreach (var name in columns.Keys) keys[name] = (null, []); await using (var command = Command("SELECT c.relname,con.conname,con.contype,array_agg(a.attname ORDER BY k.ordinality) FROM pg_constraint con JOIN pg_class c ON c.oid=con.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace JOIN unnest(con.conkey) WITH ORDINALITY k(attnum,ordinality) ON true JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=k.attnum WHERE n.nspname=@schema AND con.contype IN ('p','u') GROUP BY c.relname,con.conname,con.contype", schema)) { await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) { var constraint = new UniqueConstraint(reader.GetString(1),reader.GetFieldValue<string[]>(3)); var prior = keys[reader.GetString(0)]; keys[reader.GetString(0)] = reader.GetString(2)=="p" ? (constraint,prior.Unique) : (prior.Primary,[..prior.Unique,constraint]); } }
        var tables = columns.Select(x => new PostgreSqlTable(new TableDefinition(schema,x.Key,x.Value,keys[x.Key].Primary,keys[x.Key].Unique),x.Value)).ToArray(); var byName = tables.ToDictionary(x => x.Definition.Name,x => x.Definition); var foreignKeys = new List<ForeignKeyDefinition>(); await using (var command = Command("SELECT con.conname,c.relname,p.relname,array_agg(ca.attname ORDER BY ck.ordinality),array_agg(pa.attname ORDER BY ck.ordinality),COALESCE(bool_and(tr.tgenabled <> 'D'),true),con.convalidated FROM pg_constraint con JOIN pg_class c ON c.oid=con.conrelid JOIN pg_class p ON p.oid=con.confrelid JOIN pg_namespace n ON n.oid=c.relnamespace JOIN unnest(con.conkey) WITH ORDINALITY ck(attnum,ordinality) ON true JOIN unnest(con.confkey) WITH ORDINALITY pk(attnum,ordinality) ON pk.ordinality=ck.ordinality JOIN pg_attribute ca ON ca.attrelid=c.oid AND ca.attnum=ck.attnum JOIN pg_attribute pa ON pa.attrelid=p.oid AND pa.attnum=pk.attnum LEFT JOIN pg_trigger tr ON tr.tgconstraint=con.oid WHERE n.nspname=@schema AND con.contype='f' GROUP BY con.oid,con.conname,c.relname,p.relname,con.convalidated", schema)) { await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) foreignKeys.Add(new(reader.GetString(0),byName[reader.GetString(1)],byName[reader.GetString(2)],reader.GetFieldValue<string[]>(3),reader.GetFieldValue<string[]>(4),reader.GetBoolean(5),reader.GetBoolean(6))); }
        return new(tables,foreignKeys);
    }
    private NpgsqlCommand Command(string sql, string schema) { var command = dataSource.CreateCommand(sql); command.Parameters.AddWithValue("schema",schema); return command; }
    private static (NpgsqlDbType DbType, Type ClrType) Map(string type) => type switch { "int4" => (NpgsqlDbType.Integer,typeof(int)), "text" => (NpgsqlDbType.Text,typeof(string)), _ => throw new NotSupportedException($"PostgreSQL key type {type} is not mapped.") };
}
```

The queries use constraint ordinality, not `attnum`; read `convalidated` and trigger enforcement; and reject an unmapped type. Add the provider to the solution and integration project references.

- [ ] **Step 4: Run the tests and confirm catalog facts pass.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlCatalogReaderTests"`

Expected: one passing test proving key declaration order, nullable CLR metadata, unique-only identity metadata, ordered composite FK metadata, and target `NOT VALID` state.

- [ ] **Step 5: Commit catalog discovery.**

Run: `git add DataPitcher.sln src/DataPitcher.Providers.PostgreSql tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCatalogReaderTests.cs && git commit -m "feat: read postgres closure metadata"`

### Task 4: Identifier quoting

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/PostgreSqlIdentifier.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlIdentifierTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlIdentifierTests.cs`

- [ ] **Step 1: Write the failing quoter test.**

```csharp
[Fact]
public async Task Quote_EscapesEmbeddedQuotesAndExecutesCaseSensitiveReservedNames()
{
    const string table = "Select\"Rows";
    Assert.Equal("\"Select\"\"Rows\"", PostgreSqlIdentifier.Quote(table));
    await using var scope = await _fixture.CreateScopeAsync();
    await scope.ExecuteAsync($"CREATE TABLE {PostgreSqlIdentifier.Qualified(scope.Schema, table)} (\"Value\" integer PRIMARY KEY);");
    await scope.ExecuteAsync($"INSERT INTO {PostgreSqlIdentifier.Qualified(scope.Schema, table)} (\"Value\") VALUES (1);");
    Assert.Equal(1L, await scope.ScalarAsync<long>($"SELECT count(*) FROM {PostgreSqlIdentifier.Qualified(scope.Schema, table)};"));
}
```

- [ ] **Step 2: Run it and confirm the quoter failure.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlIdentifierTests"`

Expected: compilation fails with `The name 'PostgreSqlIdentifier' does not exist in the current context`.

- [ ] **Step 3: Implement the quoter.**

```csharp
namespace DataPitcher.Providers.PostgreSql;
public static class PostgreSqlIdentifier
{
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    public static string Qualified(string schema, string name) => Quote(schema) + "." + Quote(name);
}
```

Use this helper for every catalog-derived schema/table/column/constraint name and every generated stage name; values remain Npgsql parameters or binary COPY values.

- [ ] **Step 4: Run the test and confirm it passes.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlIdentifierTests"`

Expected: one passing test, including an identifier containing a double quote; no unquoted identifier interpolation remains.

- [ ] **Step 5: Commit quoting.**

Run: `git add src/DataPitcher.Providers.PostgreSql/PostgreSqlIdentifier.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlIdentifierTests.cs && git commit -m "feat: quote postgres identifiers"`

### Task 5: Typed staging tables

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/PostgreSqlStagingTables.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlStagingTablesTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlStagingTablesTests.cs`

- [ ] **Step 1: Write the failing concurrent-plan test.**

```csharp
[Fact]
public async Task Stages_UseTypedKeysImmutableGenerationAndDifferentPhysicalNames()
{
    await using var scope = await _fixture.CreateScopeAsync();
    var schema = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
    var table = schema.Table("declared_key").Definition;
    var selections = new Dictionary<TableDefinition, StableKeySelection> { [table] = StableKeySelector.Select(table, null) };
    await using var first = new PostgreSqlStagingTables(scope.Source, scope.Target, schema, selections);
    await using var second = new PostgreSqlStagingTables(scope.Source, scope.Target, schema, selections);
    var key = new StableKey([new("physical_second", 2), new("physical_first", 1)]);
    Assert.Single(await first.InsertSourceAsync(table, [key], 1, CancellationToken.None));
    Assert.Empty(await first.InsertSourceAsync(table, [key], 9, CancellationToken.None));
    await second.InsertSourceAsync(table, [key], 1, CancellationToken.None);
    Assert.NotEqual(first.SourceTableName(table), second.SourceTableName(table));
    Assert.Equal(1, await first.GenerationAsync(table, key, CancellationToken.None));
}
```

- [ ] **Step 2: Run it and confirm staging is missing.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlStagingTablesTests"`

Expected: compilation fails because `PostgreSqlStagingTables` does not exist.

- [ ] **Step 3: Implement typed, generated, binary-COPY stages.**

```csharp
public sealed class PostgreSqlStagingTables : IAsyncDisposable
{
    private const string OwnerSchema = "datapitcher"; private readonly string _plan = Guid.NewGuid().ToString("N"); private readonly Dictionary<TableDefinition,int> _ordinals = []; private int _nextOrdinal;
    public PostgreSqlStagingTables(NpgsqlDataSource source, NpgsqlDataSource target, PostgreSqlSchemaSnapshot schema, IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys) { Source = source; Target = target; Schema = schema; StableKeys = stableKeys; }
    public NpgsqlDataSource Source { get; } public NpgsqlDataSource Target { get; } public PostgreSqlSchemaSnapshot Schema { get; }
    public IReadOnlyDictionary<TableDefinition, StableKeySelection> StableKeys { get; }
    public string SourceTableName(TableDefinition table) => $"keys_{_plan}_{Ordinal(table):x8}";
    public async Task<IReadOnlyCollection<StableKey>> InsertSourceAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, int generation, CancellationToken ct)
    {
        await EnsureAsync(Source, SourceTableName(table), table, ct); await EnsureAsync(Source, InputTableName(table), table, ct); await ExecuteAsync(Source, "TRUNCATE " + Qualified(InputTableName(table)), ct); await CopyAsync(Source, InputTableName(table), table, keys, generation, ct);
        return await InsertReturningAsync(table, generation, ct);
    }
    public async Task ReplaceSourceCandidatesAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, CancellationToken ct) { await EnsureAsync(Source, InputTableName(table), table, ct); await ExecuteAsync(Source, "TRUNCATE " + Qualified(InputTableName(table)), ct); await CopyAsync(Source, InputTableName(table), table, keys, 0, ct); }
    public async Task ReplaceTargetCandidatesAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, CancellationToken ct) { await EnsureAsync(Target, TargetTableName(table), table, ct); await ExecuteAsync(Target, "TRUNCATE " + Qualified(TargetTableName(table)), ct); await CopyAsync(Target, TargetTableName(table), table, keys, 0, ct); }
    public async Task<int> GenerationAsync(TableDefinition table, StableKey key, CancellationToken ct) { var columns = KeyColumns(table); await using var command = Source.CreateCommand("SELECT __generation FROM " + Qualified(SourceTableName(table)) + " WHERE " + string.Join(" AND ", columns.Select((_, i) => $"k{i} = @p{i}"))); for (var i = 0; i < columns.Count; i++) command.Parameters.AddWithValue($"p{i}", key.Components.Single(x => x.Column == columns[i]).Value!); return (int)(await command.ExecuteScalarAsync(ct))!; }
    public string TargetTableName(TableDefinition table) => $"target_{_plan}_{Ordinal(table):x8}";
    public string InputTableName(TableDefinition table) => $"input_{_plan}_{Ordinal(table):x8}";
    private int Ordinal(TableDefinition table) => _ordinals.TryGetValue(table, out var value) ? value : _ordinals[table] = _nextOrdinal++;
    private IReadOnlyList<string> KeyColumns(TableDefinition table) => StableKeys[table].Constraint!.Columns;
    private static string Qualified(string table) => PostgreSqlIdentifier.Qualified(OwnerSchema, table);
    private async Task EnsureAsync(NpgsqlDataSource dataSource, string name, TableDefinition table, CancellationToken ct) { var columns = KeyColumns(table); var metadata = Schema.Tables.Single(x => x.Definition == table); var declarations = string.Join(", ", columns.Select((column, i) => $"k{i} {metadata.Column(column).StoreType} NOT NULL")); var unique = string.Join(", ", columns.Select((_, i) => $"k{i}")); await ExecuteAsync(dataSource, $"CREATE SCHEMA IF NOT EXISTS {PostgreSqlIdentifier.Quote(OwnerSchema)}; CREATE TABLE IF NOT EXISTS {Qualified(name)} ({declarations}, __generation integer NOT NULL, UNIQUE ({unique}))", ct); }
    private async Task CopyAsync(NpgsqlDataSource dataSource, string name, TableDefinition table, IReadOnlyCollection<StableKey> keys, int generation, CancellationToken ct) { var columns = KeyColumns(table); var metadata = Schema.Tables.Single(x => x.Definition == table); var names = string.Join(", ", columns.Select((_, i) => $"k{i}").Append("__generation")); await using var connection = await dataSource.OpenConnectionAsync(ct); await using var importer = await connection.BeginBinaryImportAsync($"COPY {Qualified(name)} ({names}) FROM STDIN (FORMAT BINARY)", ct); foreach (var key in keys) { await importer.StartRowAsync(ct); foreach (var column in columns) await importer.WriteAsync(key.Components.Single(x => x.Column == column).Value, metadata.Column(column).NpgsqlDbType, ct); await importer.WriteAsync(generation, NpgsqlDbType.Integer, ct); } await importer.CompleteAsync(ct); }
    private async Task<IReadOnlyCollection<StableKey>> InsertReturningAsync(TableDefinition table, int generation, CancellationToken ct) { var columns = KeyColumns(table); var names = string.Join(", ", columns.Select((_, i) => $"k{i}")); var sql = $"INSERT INTO {Qualified(SourceTableName(table))} ({names}, __generation) SELECT {names}, @generation FROM {Qualified(InputTableName(table))} ON CONFLICT ({names}) DO NOTHING RETURNING {names}"; await using var command = Source.CreateCommand(sql); command.Parameters.AddWithValue("generation", generation); await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<StableKey>(); while (await reader.ReadAsync(ct)) rows.Add(new(columns.Select((column, i) => new KeyComponent(column, reader.GetValue(i))))); return rows; }
    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, CancellationToken ct) { await using var command = dataSource.CreateCommand(sql); await command.ExecuteNonQueryAsync(ct); }
    private async ValueTask DropOwnedTablesAsync() { foreach (var table in _ordinals.Keys) foreach (var dataSource in new[] { Source, Target }) foreach (var name in new[] { SourceTableName(table), InputTableName(table), TargetTableName(table) }) await ExecuteAsync(dataSource, "DROP TABLE IF EXISTS " + Qualified(name), CancellationToken.None); }
    public ValueTask DisposeAsync() => DropOwnedTablesAsync();
}
```

Stages use catalog store types, binary COPY, `UNIQUE (k0,...)`, and conflict-ignore insertion; the target bridge is truncated and COPY-filled for each probe. Disposal drops only names generated from the plan token and ordinal.

- [ ] **Step 4: Run the staging test and confirm it passes.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlStagingTablesTests"`

Expected: one passing test; both plans accept the same composite key without collision, the source catalog type is used for each `kN`, duplicate insertion preserves generation 1, and disposal removes generated stages.

- [ ] **Step 5: Commit staging.**

Run: `git add src/DataPitcher.Providers.PostgreSql/PostgreSqlStagingTables.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlStagingTablesTests.cs && git commit -m "feat: stage typed postgres closure keys"`

### Task 6: The four store operations in real SQL

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/PostgreSqlClosureStore.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureStoreTests.cs`

- [ ] **Step 1: Write the failing positional-composite store test.**

```csharp
[Fact]
public async Task ExpandAsync_UsesForeignKeyPositionRatherThanPhysicalColumnOrder()
{
    await using var scope = await _fixture.CreateScopeAsync();
    await scope.ExecuteAsync("INSERT INTO composite_parent VALUES (7,8); INSERT INTO composite_child VALUES (1,8,7);");
    var catalog = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
    var child = catalog.Table("composite_child").Definition; var parent = catalog.Table("composite_parent").Definition;
    var relationship = new ClosureRelationship(catalog.ForeignKey("fk_composite_child_parent"));
    await using var store = new PostgreSqlClosureStore(scope.Source, scope.Target, catalog, catalog, new Dictionary<TableDefinition, StableKeySelection> { [child] = StableKeySelector.Select(child, null), [parent] = StableKeySelector.Select(parent, null) });
    var result = await store.ExpandAsync(relationship, [new StableKey([new("id", 1)])], CancellationToken.None);
    Assert.Contains(result, key => key == new StableKey([new("left_value", 7), new("right_value", 8)]));
}
```

- [ ] **Step 2: Run it and confirm the store does not exist.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~ExpandAsync_UsesForeignKeyPosition"`

Expected: compilation fails with `The type or namespace name 'PostgreSqlClosureStore' could not be found`.

- [ ] **Step 3: Implement the existing seam with set-based SQL.**

```csharp
public sealed class PostgreSqlClosureStore : IClosureStore, IAsyncDisposable
{
    private readonly PostgreSqlStagingTables _stages; private readonly PostgreSqlSchemaSnapshot _source; private readonly PostgreSqlSchemaSnapshot _target;
    public PostgreSqlClosureStore(NpgsqlDataSource source, NpgsqlDataSource target, PostgreSqlSchemaSnapshot sourceSchema, PostgreSqlSchemaSnapshot targetSchema, IReadOnlyDictionary<TableDefinition, StableKeySelection> keys, IReadOnlyCollection<PostgreSqlManualRelationship>? manual = null)
    { _stages = new(source, target, sourceSchema, keys); _source = sourceSchema; _target = targetSchema; StableKeys = keys; ManualRelationships = manual?.ToArray() ?? []; }
    public IReadOnlyDictionary<TableDefinition, StableKeySelection> StableKeys { get; }
    public IReadOnlyCollection<PostgreSqlManualRelationship> ManualRelationships { get; }
    public Task<IReadOnlyCollection<StableKey>> SeedRootKeysAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, CancellationToken ct) => _stages.InsertSourceAsync(table, keys, 0, ct);
    public async Task<IReadOnlyDictionary<StableKey, TargetProbe>> ProbeTargetAsync(TableDefinition table, IReadOnlyCollection<ClosureRelationship> outgoing, IReadOnlyCollection<StableKey> keys, CancellationToken ct)
    { await _stages.ReplaceTargetCandidatesAsync(table, keys, ct); return await ProbeAsync(table, outgoing, keys, ct); }
    public Task<IReadOnlyCollection<StableKey>> ExpandAsync(ClosureRelationship relationship, IReadOnlyCollection<StableKey> keys, CancellationToken ct) => ExpandSetAsync(relationship, keys, ct);
    public Task<IReadOnlyCollection<StableKey>> InsertNewKeysAsync(TableDefinition table, IReadOnlyCollection<StableKey> keys, int generation, CancellationToken ct) => _stages.InsertSourceAsync(table, keys, generation, ct);
    public ValueTask DisposeAsync() => _stages.DisposeAsync();
    private async Task<IReadOnlyDictionary<StableKey, TargetProbe>> ProbeAsync(TableDefinition table, IReadOnlyCollection<ClosureRelationship> outgoing, IReadOnlyCollection<StableKey> keys, CancellationToken ct)
    {
        var columns = KeyColumns(table); var select = string.Join(", ", columns.Select((_, i) => $"s.k{i}")); var join = string.Join(" AND ", columns.Select((column, i) => $"s.k{i} = t.{PostgreSqlIdentifier.Quote(column)}"));
        var sql = $"/* DataPitcher.ProbeTarget */ SELECT {select}, t.{PostgreSqlIdentifier.Quote(columns[0])} IS NOT NULL FROM {PostgreSqlIdentifier.Qualified("datapitcher", _stages.TargetTableName(table))} s LEFT JOIN {Qualified(table)} t ON {join}";
        var states = outgoing.ToDictionary(x => x, TargetState); var result = new Dictionary<StableKey, TargetProbe>(); await using var command = _stages.Target.CreateCommand(sql); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadKey(reader, table), new TargetProbe(reader.GetBoolean(columns.Count), states)); return result;
    }
    private async Task<IReadOnlyCollection<StableKey>> ExpandSetAsync(ClosureRelationship relationship, IReadOnlyCollection<StableKey> keys, CancellationToken ct)
    {
        await _stages.ReplaceSourceCandidatesAsync(relationship.FromTable, keys, ct); var (fromColumns, toColumns) = JoinColumns(relationship); var fromKeys = KeyColumns(relationship.FromTable); var toKeys = KeyColumns(relationship.ToTable);
        var select = string.Join(", ", toKeys.Select(column => $"t.{PostgreSqlIdentifier.Quote(column)}")); var sourceJoin = string.Join(" AND ", fromKeys.Select((column, i) => $"s.k{i} = f.{PostgreSqlIdentifier.Quote(column)}")); var relationshipJoin = string.Join(" AND ", fromColumns.Zip(toColumns).Select(pair => $"f.{PostgreSqlIdentifier.Quote(pair.First)} = t.{PostgreSqlIdentifier.Quote(pair.Second)}"));
        var required = string.Join(" AND ", fromColumns.Select(column => $"f.{PostgreSqlIdentifier.Quote(column)} IS NOT NULL")); var sql = $"SELECT DISTINCT {select} FROM {PostgreSqlIdentifier.Qualified("datapitcher", _stages.InputTableName(relationship.FromTable))} s JOIN {Qualified(relationship.FromTable)} f ON {sourceJoin} JOIN {Qualified(relationship.ToTable)} t ON {relationshipJoin} WHERE {required}";
        await using var command = _stages.Source.CreateCommand(sql); await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<StableKey>(); while (await reader.ReadAsync(ct)) result.Add(ReadKey(reader, relationship.ToTable)); return result;
    }
    private (IReadOnlyList<string> From, IReadOnlyList<string> To) JoinColumns(ClosureRelationship relationship)
    { if (relationship.ForeignKey is { } fk) return relationship.IsInbound ? (fk.ParentColumns, fk.ChildColumns) : (fk.ChildColumns, fk.ParentColumns); var manual = ManualRelationships.Single(x => x.Relationship.Equals(relationship)); return (manual.FromColumns, manual.ToColumns); }
    private TargetConstraintState TargetState(ClosureRelationship relationship)
    { if (relationship.ForeignKey is not { } sourceFk) return new(relationship.Name, false, false, false); var fk = _target.ForeignKeys.SingleOrDefault(x => x.ChildTable == sourceFk.ChildTable && x.ParentTable == sourceFk.ParentTable && x.ChildColumns.SequenceEqual(sourceFk.ChildColumns) && x.ParentColumns.SequenceEqual(sourceFk.ParentColumns)); return fk is null ? new(relationship.Name, false, false, false) : new(fk.Name, true, fk.IsEnforced, fk.IsTrusted); }
    private IReadOnlyList<string> KeyColumns(TableDefinition table) => StableKeys[table].Constraint!.Columns;
    private StableKey ReadKey(NpgsqlDataReader reader, TableDefinition table) { var columns = KeyColumns(table); var metadata = _source.Tables.Single(x => x.Definition == table); var components = columns.Select((column, i) => new KeyComponent(column, reader.GetValue(i))).ToArray(); if (components.Where((component, i) => component.Value?.GetType() != metadata.Column(columns[i]).ClrType).Any()) throw new InvalidOperationException("Stable-key CLR type does not match catalog metadata."); return new StableKey(components); }
    private static string Qualified(TableDefinition table) => PostgreSqlIdentifier.Qualified(table.Schema, table.Name);
}
```

`ProbeAsync` emits exactly one tagged `/* DataPitcher.ProbeTarget */` target `SELECT` with a left join from its target-stage `kN` columns to the target table’s selected stable key columns. Build one `TargetProbe` per returned staged key; each outgoing relationship resolves its target catalog FK by child/parent table and ordered columns, returning absent/disabled/unvalidated as `TargetConstraintState(..., false, false, false)` or its actual `IsEnforced`/`IsTrusted` values. It must not use `IN`.

`ExpandSetAsync` binary-COPYs `keys` into the source input stage, then joins input stage → `relationship.FromTable` on its selected stable key → `relationship.ToTable`; generate `childColumn[i] = parentColumn[i]` in the stored list order, add `childColumn IS NOT NULL` for every pair, and return the target table’s selected stable key columns. The inbound branch reverses that join. A configured `PostgreSqlManualRelationship` uses the same positional join form. Materialize each `StableKey` with catalog column names and assert `value.GetType() == PostgreSqlColumn.ClrType` before returning it. Thus native types, composite order, and provider equality survive every stage and join.

- [ ] **Step 4: Run the store tests and confirm they pass.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~ExpandAsync_UsesForeignKeyPosition"`

Expected: the test passes only for `(child_right, child_left) -> (left_value, right_value)`; swapping either positional list makes its required parent absent and fails the assertion.

- [ ] **Step 5: Commit the real store.**

Run: `git add src/DataPitcher.Providers.PostgreSql/PostgreSqlClosureStore.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureStoreTests.cs && git commit -m "feat: add postgres closure store"`

### Task 7: Re-run Slice 1’s closure tests against the real store

**Files:**
- Create: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlDependencyClosureTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlDependencyClosureTests.cs`

- [ ] **Step 1: Write the failing real-store contract suite.**

```csharp
public sealed class PostgreSqlDependencyClosureTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture; public PostgreSqlDependencyClosureTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;
    [Fact] public async Task Closure_WhenParentSatisfiedOnOnePathButRequiredOnAnother_StillTransfersSharedAncestor()
    { await using var s = await Scenario.CreateAsync(_fixture); var (r,a,b,x,ra,rb,ax,bx) = await s.DiamondAsync(targetHasA: true); var result = await s.RunAsync([ra,rb,ax,bx], s.Root(r,1)); Assert.True(result.Contains(x,s.K(4))); }
    [Fact] public async Task Closure_WhenParentTargetConstraintIsUntrusted_TransfersParentAndNamesTheConstraint()
    { await using var s = await Scenario.CreateAsync(_fixture); var (c,p,g,cp,pg) = await s.UntrustedAsync(); var result = await s.RunAsync([cp,pg],s.Root(c,1)); Assert.True(result.Contains(p,s.K(2))); Assert.True(result.Contains(g,s.K(3))); Assert.Contains(new TargetConstraintWarning($"Target_{pg.Name}"),result.Warnings); }
}
```

`Scenario` provisions each fixture as actual tables and rows in a new scope, constructs the real store from source and target catalog snapshots, and returns real `TableDefinition`, `ClosureRelationship`, and `StableKey` values. Keep every assertion from `DependencyClosureTests.cs` lines 5–35 byte-for-byte. Translate `Link`/`AddRow` setup to source `INSERT`s, `MarkTarget` to target `INSERT`s, and manual/inbound fixtures to configured positional `PostgreSqlManualRelationship` or reverse FK joins. Do not edit the Slice 1 test file.

- [ ] **Step 2: Run the suite and confirm the first real-engine failure.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlDependencyClosureTests"`

Expected: the copied suite fails while `PostgreSqlClosureStore` does not yet return real staged expansion/probe results; the diamond assertion is the first required red failure.

- [ ] **Step 3: Complete the scenario translation and all thirty-one unchanged assertions.**

```csharp
private async Task<ClosureResult> RunAsync(ClosureRelationship[] relationships, params ClosureRoot[] roots)
{
    var tables = roots.Select(x => x.Table).Concat(relationships.SelectMany(x => new[] { x.FromTable, x.ToTable })).Distinct();
    return await new DependencyClosure(Store).ComputeAsync(new ClosureRequest(roots, relationships, tables.ToDictionary(x => x, x => StableKeySelector.Select(x, null))), CancellationToken.None);
}
```

Include the child/parent, default inbound exclusion, explicit inbound, nullable FK, composite FK, unique-reference, dual-FK, manual, orphan, shared-parent, three root policies, trusted/absent/untrusted/disabled target-constraint, disabled relationship, self-cycle, two-table cycle, breadth-first generation, blocked/no-selection/non-null-unique cases, and both diamond assertions from the existing first thirty-one facts. The target `NOT VALID` fixture contains the target parent but omits its grandparent, so satisfaction is rejected and the existing warning assertion names `Target_FK_P_G`.

- [ ] **Step 4: Run the named point-of-the-slice verification.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlDependencyClosureTests"`

Expected: `Passed: 31. Failed: 0.` Every copied assertion passes unchanged against separate real source and target PostgreSQL containers; if any assertion needs alteration, stop and report a seam finding instead of weakening it.

- [ ] **Step 5: Commit the real-engine behavioural re-run.**

Run: `git add tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlDependencyClosureTests.cs && git commit -m "test: rerun closure contract against postgres"`

### Task 8: Prove the batching claim

**Files:**
- Create: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCommandRecorder.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlProbeBatchingTests.cs`
- Modify: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureFixture.cs`
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlProbeBatchingTests.cs`

- [ ] **Step 1: Write the failing emitted-command test.**

```csharp
[Fact]
public async Task ComputeAsync_ProbesEachTableOncePerGeneration_NotOncePerKey()
{
    await using var scenario = await Scenario.CreateAsync(_fixture, new PostgreSqlCommandRecorder());
    var (children,parent,grandparent,cp,pg) = await scenario.TwoRootChainAsync();
    await scenario.RunAsync([cp,pg], scenario.Root(children,1,2));
    Assert.Equal(1, scenario.Commands.Count("DataPitcher.ProbeTarget", children));
    Assert.Equal(1, scenario.Commands.Count("DataPitcher.ProbeTarget", parent));
    Assert.Equal(1, scenario.Commands.Count("DataPitcher.ProbeTarget", grandparent));
    Assert.Equal(3, scenario.Commands.Count("DataPitcher.ProbeTarget"));
}
```

- [ ] **Step 2: Run it and confirm the recorder is absent.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlProbeBatchingTests"`

Expected: compilation fails with `The type or namespace name 'PostgreSqlCommandRecorder' could not be found`.

- [ ] **Step 3: Add the Npgsql execution recorder and inject it into the fixture data source.**

```csharp
internal sealed class PostgreSqlCommandRecorder : ILoggerFactory, ILogger
{
    private readonly ConcurrentQueue<string> _messages = []; public ILogger CreateLogger(string _) => this;
    public void AddProvider(ILoggerProvider _) { } public void Dispose() { } public IDisposable? BeginScope<T>(T state) where T : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;
    public void Log<T>(LogLevel level, EventId id, T state, Exception? error, Func<T, Exception?, string> format)
    { var message = format(state, error); if (message.Contains("DataPitcher.ProbeTarget", StringComparison.Ordinal)) _messages.Enqueue(message); }
    public int Count(string tag, TableDefinition? table = null) => _messages.Count(x => x.Contains(tag, StringComparison.Ordinal) && (table is null || x.Contains(PostgreSqlIdentifier.Qualified(table.Schema, table.Name), StringComparison.Ordinal)));
}
```

When a recorder is supplied, fixture data-source construction uses `var builder = new NpgsqlDataSourceBuilder(connectionString); builder.UseLoggerFactory(recorder); return builder.Build();`. This is a driver database-command execution log, not a store method-call counter. Keep the SQL comment emitted by Task 6 on the one target `SELECT`; binary COPY has no probe tag and is deliberately not counted as an existence probe.

- [ ] **Step 4: Run the batching proof and confirm it passes.**

Run: `./scripts/test-postgres.sh`

Expected: all PostgreSQL integration tests pass with 100% coverage; the batching test observes one target probe per frontier table, never one per key or a large `IN` list.

- [ ] **Step 5: Commit the batching proof.**

Run: `git add tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCommandRecorder.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlProbeBatchingTests.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlClosureFixture.cs && git commit -m "test: prove postgres target probe batching"`

## Self-Review

- [ ] Normative coverage: sections 3 and 8 are covered by catalog-derived ordered stable keys, CLR-type guards, typed native staging, composite/nullable/unique FK tests, cycles, and diamonds. Sections 4–6 are covered by Core’s generation barrier plus conflict-policy re-run, conflict-ignoring immutable stage insertion, source set expansion, target staging probe, and catalog-based enforced/validated satisfaction. The plan explicitly covers the `NOT VALID` warning path and no-per-key probe rule.
- [ ] Deferrals: full provider capability detection, broad PostgreSQL type/dialect support, durable staging TTL cleanup, SQL Server, transfer writers, and broader cross-provider compatibility remain excluded because this slice proves the seam first.
- [ ] Type and method-name consistency checked across all eight tasks: `PostgreSqlSchemaSnapshot`, `PostgreSqlStagingTables`, `PostgreSqlClosureStore`, `PostgreSqlClosureFixture`, `Scenario`, `PostgreSqlCommandRecorder`, `SeedRootKeysAsync`, `ProbeTargetAsync`, `ExpandAsync`, and `InsertNewKeysAsync` use the same names and signatures throughout.
