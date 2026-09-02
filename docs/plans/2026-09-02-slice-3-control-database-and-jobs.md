# DataPitcher Slice 3: Control Database and Job Orchestration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a SQLite-backed control database that durably starts, transitions, leases, fences, and observes transfer jobs without becoming part of transfer correctness.

**Architecture:** `DataPitcher.Infrastructure` owns the SQLite-only control database, so a separate control-storage project would be ceremony around one implementation. Core owns the provider-independent job-state transition rules; Infrastructure persists their outcomes with LINQ to DB and explicit SQL migrations. The target database remains the authoritative checkpoint and fence adjudicator for transfer writes; SQLite retains scheduling state and a write-only derived checkpoint mirror.

**Tech Stack:** .NET SDK 10.0.400, C# latest, SQLite in process, LINQ to DB 6.4.0, Microsoft.Data.Sqlite 10.0.11, xUnit 2.9.3, Coverlet collector 6.0.4, ReportGenerator, Bash.

---

## File Structure

- `DataPitcher.sln` — adds the Infrastructure project to the solution.
- `src/DataPitcher.Core/Jobs/JobState.cs` — job-state vocabulary, legal-transition table, and rejection exception.
- `src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj` — SQLite-only Infrastructure assembly with exact LINQ to DB and SQLite package pins.
- `src/DataPitcher.Infrastructure/Storage/ControlDatabase.cs` — opens a LINQ to DB SQLite control connection with foreign keys enabled.
- `src/DataPitcher.Infrastructure/Time/IClock.cs` — injectable UTC clock and production system-clock implementation.
- `src/DataPitcher.Infrastructure/Leasing/LeaseGrant.cs` — immutable owner, expiry, renewal, and fence-token lease value.
- `src/DataPitcher.Infrastructure/Migrations/ControlDatabaseMigrator.cs` — applies embedded, ordered SQL scripts exactly once.
- `src/DataPitcher.Infrastructure/Migrations/0001-initial.sql` — initial `SchemaVersion`, jobs, transitions, lease, and mirror schema.
- `src/DataPitcher.Infrastructure/Persistence/ControlRows.cs` — internal LINQ to DB mappings for the jobs, state-history, and lease tables.
- `src/DataPitcher.Infrastructure/Leasing/LeaseStore.cs` — conditional acquisition and fenced renewal against SQLite rows.
- `src/DataPitcher.Infrastructure/Persistence/JobStore.cs` — idempotent starts and persisted, lease-fenced job transitions.
- `src/DataPitcher.Infrastructure/Checkpoints/CheckpointMirrorStore.cs` — write-only persistence for the derived target-checkpoint mirror.
- `tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj` — references Infrastructure for in-process SQLite unit tests.
- `tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseTests.cs` — verifies the SQLite LINQ to DB connection.
- `tests/DataPitcher.UnitTests/Jobs/JobStateMachineTests.cs` — exhaustively verifies legal and rejected state transitions.
- `tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseFixture.cs` — disposable migrated SQLite fixture and deterministic manual clock.
- `tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseMigratorTests.cs` — verifies script versioning and idempotent migration application.
- `tests/DataPitcher.UnitTests/Infrastructure/LeaseStoreTests.cs` — verifies conditional acquisition and clock-driven renewal.
- `tests/DataPitcher.UnitTests/Infrastructure/JobStoreTests.cs` — verifies idempotent starts, transition history, and stale-fence rejection.
- `tests/DataPitcher.UnitTests/Infrastructure/CheckpointMirrorStoreTests.cs` — verifies derived mirror upserts without exposing a production read API.
- `tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj` — references Infrastructure for the mirror-boundary architecture test.
- `tests/DataPitcher.ArchitectureTests/CheckpointMirrorBoundaryTests.cs` — prevents production reads of SQLite checkpoint mirrors.
- `docs/test-coverage-matrix.md` — traces this slice’s orchestration and checkpoint-boundary evidence.
- `docs/plans/2026-09-02-slice-3-control-database-and-jobs.md` — this implementation plan.

## Scope and Deferrals

SQLite runs in process, so all new tests belong in `scripts/test-unit.sh` and need no Docker. `scripts/test-postgres.sh` remains the PostgreSQL integration lane, while only `scripts/test-all.sh` merges coverage with ReportGenerator and enforces 100 percent line, branch, and method coverage for handwritten production code. Warnings are errors through `Directory.Build.props`; each task must keep that rule intact.

The persisted state set is `Draft`, `Queued`, `Preparing`, `Running`, `Pausing`, `Paused`, `Cancelling`, `Cancelled`, `Verifying`, `Succeeded`, `Failed`, and `VerificationFailed`. A state change is valid only when `JobStateMachine` allows it, and every successful change inserts one `JobStateTransitions` history row in the same SQLite transaction as the row update.

ADR 0001 is non-negotiable: the authoritative `(job_id, run_id)` checkpoint, including manifest seal, stable key, row count, and fence token, lives in the **target** database and is written with target apply work in one target transaction. The control database mirror is derived display and scheduling data only; no recovery, resume, apply, retry, or verification decision may read it. This plan establishes the control-side lease token and demonstrates a stale guarded SQLite work write affects zero rows. PostgreSQL target-checkpoint creation, target staging, and target apply fencing are transfer-execution work; that later code must repeat the same stale-worker proof against the target checkpoint transaction.

This document uses the requested Slice 3 label. `docs/roadmap.md` currently labels the same control-database scope as Slice 2; reconciling that editorial numbering is outside this implementation plan. Authentication, API/background-service hosting, target checkpoint reads, recovery, target mutation journals, provider bulk writes, and Docker-backed transfer tests are deferred.

### Task 1: Infrastructure project, SQLite connection, and time seam

**Files:**
- Create: `src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj`, `src/DataPitcher.Infrastructure/Storage/ControlDatabase.cs`, `src/DataPitcher.Infrastructure/Time/IClock.cs`, `src/DataPitcher.Infrastructure/Leasing/LeaseGrant.cs`, `tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseTests.cs`
- Modify: `DataPitcher.sln`, `tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj`
- Test: `tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseTests.cs`

- [ ] **Step 1: Write the failing connection test and add its project reference.**

```xml
<!-- append to tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj -->
<ItemGroup><ProjectReference Include="../../src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj" /></ItemGroup>
```

```csharp
using DataPitcher.Infrastructure.Storage;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class ControlDatabaseTests
{
    [Fact]
    public void ControlDatabase_WhenOpened_ExecutesSQLiteThroughLinqToDb()
    {
        using var connection = new ControlDatabase("Data Source=:memory:").Open();
        Assert.Single(connection.QueryToList<int>("SELECT 1"));
    }
}
```

- [ ] **Step 2: Run the focused test and confirm the missing-project failure.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~ControlDatabaseTests"`

Expected: build fails with `MSB3202` because `src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj` does not exist.

- [ ] **Step 3: Create the Infrastructure assembly, connection, clock, and lease value.**

```xml
<!-- src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj -->
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>DataPitcher.Infrastructure</RootNamespace></PropertyGroup><ItemGroup><ProjectReference Include="../DataPitcher.Core/DataPitcher.Core.csproj" /><PackageReference Include="linq2db" Version="6.4.0" /><PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.11" /></ItemGroup></Project>
```

```csharp
// Storage/ControlDatabase.cs
using LinqToDB;
using LinqToDB.Data;

namespace DataPitcher.Infrastructure.Storage;

public sealed class ControlDatabase(string connectionString)
{
    public DataConnection Open()
    {
        var connection = new DataConnection(ProviderName.SQLiteMS, connectionString);
        connection.Execute("PRAGMA foreign_keys = ON;");
        return connection;
    }
}

// Time/IClock.cs
namespace DataPitcher.Infrastructure.Time;

public interface IClock { DateTimeOffset UtcNow { get; } }

public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

// Leasing/LeaseGrant.cs
namespace DataPitcher.Infrastructure.Leasing;

public sealed record LeaseGrant(Guid JobId, string OwnerId, long FenceToken, DateTimeOffset ExpiresUtc, DateTimeOffset RenewAfterUtc);
```

Run: `dotnet sln DataPitcher.sln add src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj`

- [ ] **Step 4: Run the focused test and confirm it passes.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~ControlDatabaseTests"`

Expected: `Passed: 1. Failed: 0.`

- [ ] **Step 5: Commit the Infrastructure foundation.**

Run: `git add DataPitcher.sln src/DataPitcher.Infrastructure tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseTests.cs && git commit -m "feat: add SQLite control database foundation"`

### Task 2: Core job-state transition rules

**Files:**
- Create: `src/DataPitcher.Core/Jobs/JobState.cs`, `tests/DataPitcher.UnitTests/Jobs/JobStateMachineTests.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Jobs/JobStateMachineTests.cs`

- [ ] **Step 1: Write exhaustive legal and illegal transition tests.**

```csharp
using DataPitcher.Core.Jobs;
using Xunit;

namespace DataPitcher.UnitTests.Jobs;

public sealed class JobStateMachineTests
{
    private static readonly HashSet<(JobState From, JobState To)> Allowed =
    [
        (JobState.Draft, JobState.Queued), (JobState.Draft, JobState.Cancelling),
        (JobState.Queued, JobState.Preparing), (JobState.Queued, JobState.Cancelling),
        (JobState.Preparing, JobState.Running), (JobState.Preparing, JobState.Pausing), (JobState.Preparing, JobState.Cancelling), (JobState.Preparing, JobState.Failed),
        (JobState.Running, JobState.Pausing), (JobState.Running, JobState.Cancelling), (JobState.Running, JobState.Verifying), (JobState.Running, JobState.Failed),
        (JobState.Pausing, JobState.Paused), (JobState.Pausing, JobState.Cancelling), (JobState.Pausing, JobState.Failed),
        (JobState.Paused, JobState.Queued), (JobState.Paused, JobState.Cancelling),
        (JobState.Cancelling, JobState.Cancelled), (JobState.Cancelling, JobState.Failed),
        (JobState.Verifying, JobState.Succeeded), (JobState.Verifying, JobState.Failed), (JobState.Verifying, JobState.VerificationFailed),
    ];
    public static IEnumerable<object[]> StatePairs() => Enum.GetValues<JobState>().SelectMany(from => Enum.GetValues<JobState>().Select(to => new object[] { from, to }));

    [Theory]
    [MemberData(nameof(StatePairs))]
    public void JobStateMachine_ForEveryStatePair_AcceptsOnlyTheSpecifiedTransitions(JobState from, JobState to)
    {
        Assert.Equal(Allowed.Where(pair => pair.From == from).Select(pair => pair.To), JobStateMachine.ValidTargets(from));
        if (Allowed.Contains((from, to)))
            JobStateMachine.EnsureTransition(from, to);
        else
            Assert.Throws<InvalidJobStateTransitionException>(() => JobStateMachine.EnsureTransition(from, to));
    }
}
```

- [ ] **Step 2: Run the focused test and confirm the missing namespace failure.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~JobStateMachineTests"`

Expected: compilation fails with `CS0234` because `DataPitcher.Core.Jobs` does not exist.

- [ ] **Step 3: Add the complete state vocabulary and transition table.**

```csharp
namespace DataPitcher.Core.Jobs;

public enum JobState { Draft, Queued, Preparing, Running, Pausing, Paused, Cancelling, Cancelled, Verifying, Succeeded, Failed, VerificationFailed }

public sealed class InvalidJobStateTransitionException(JobState from, JobState to)
    : InvalidOperationException($"Job cannot transition from {from} to {to}.");

public static class JobStateMachine
{
    public static IReadOnlyList<JobState> ValidTargets(JobState from) => from switch
    {
        JobState.Draft => [JobState.Queued, JobState.Cancelling],
        JobState.Queued => [JobState.Preparing, JobState.Cancelling],
        JobState.Preparing => [JobState.Running, JobState.Pausing, JobState.Cancelling, JobState.Failed],
        JobState.Running => [JobState.Pausing, JobState.Cancelling, JobState.Verifying, JobState.Failed],
        JobState.Pausing => [JobState.Paused, JobState.Cancelling, JobState.Failed],
        JobState.Paused => [JobState.Queued, JobState.Cancelling],
        JobState.Cancelling => [JobState.Cancelled, JobState.Failed],
        JobState.Verifying => [JobState.Succeeded, JobState.Failed, JobState.VerificationFailed],
        _ => [],
    };

    public static void EnsureTransition(JobState from, JobState to)
    {
        if (!ValidTargets(from).Contains(to))
            throw new InvalidJobStateTransitionException(from, to);
    }
}
```

- [ ] **Step 4: Run the focused test and confirm every state pair is covered.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~JobStateMachineTests"`

Expected: all generated state-pair assertions pass with `Failed: 0.`

- [ ] **Step 5: Commit the state-machine contract.**

Run: `git add src/DataPitcher.Core/Jobs/JobState.cs tests/DataPitcher.UnitTests/Jobs/JobStateMachineTests.cs && git commit -m "feat: define persisted job state transitions"`

### Task 3: Versioned control-schema migration

**Files:**
- Create: `src/DataPitcher.Infrastructure/Migrations/ControlDatabaseMigrator.cs`, `src/DataPitcher.Infrastructure/Migrations/0001-initial.sql`, `src/DataPitcher.Infrastructure/Persistence/ControlRows.cs`, `tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseFixture.cs`, `tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseMigratorTests.cs`
- Modify: `src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj`
- Test: `tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseMigratorTests.cs`

- [ ] **Step 1: Write the failing migration test and reusable deterministic fixture.**

```csharp
// ControlDatabaseFixture.cs
using DataPitcher.Infrastructure.Migrations;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;

namespace DataPitcher.UnitTests.Infrastructure;

internal sealed class ManualClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;
    public void Advance(TimeSpan elapsed) => UtcNow = UtcNow.Add(elapsed);
}

internal sealed class ControlDatabaseFixture : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"datapitcher-{Guid.NewGuid():N}.db");
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
    public ControlDatabase Database { get; }
    public ControlDatabaseMigrator Migrator { get; }
    public ControlDatabaseFixture() { Database = new($"Data Source={_path}"); Migrator = new(Database, Clock); }
    public Guid SeedJob()
    {
        var jobId = Guid.NewGuid(); var runId = Guid.NewGuid(); var stamp = Clock.UtcNow.ToString("O");
        using var db = Database.Open();
        db.Execute($"INSERT INTO Jobs (JobId, RunId, PlanId, IdempotencyKey, State, CreatedUtc, UpdatedUtc) VALUES ('{jobId}', '{runId}', '{Guid.NewGuid()}', '{Guid.NewGuid():N}', 'Queued', '{stamp}', '{stamp}');");
        db.Execute($"INSERT INTO JobLeases (JobId, OwnerId, ExpiresUtc, FenceToken) VALUES ('{jobId}', NULL, NULL, 0);");
        return jobId;
    }
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }
}

// ControlDatabaseMigratorTests.cs
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class ControlDatabaseMigratorTests
{
    [Fact]
    public void ControlDatabaseMigrator_WhenAppliedTwice_RunsVersionOneExactlyOnce()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply(); fixture.Migrator.Apply();
        using var db = fixture.Database.Open();
        Assert.Equal([1], db.QueryToList<int>("SELECT Version FROM SchemaVersion ORDER BY Version"));
        Assert.Contains("JobLeases", db.QueryToList<string>("SELECT name FROM sqlite_master WHERE type = 'table'"));
    }
}
```

- [ ] **Step 2: Run the focused test and confirm the absent migrator failure.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~ControlDatabaseMigratorTests"`

Expected: compilation fails with `CS0246` because `ControlDatabaseMigrator` is not defined.

- [ ] **Step 3: Add the embedded SQL migration, mappings, and migration runner.**

```xml
<!-- append to src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj -->
<ItemGroup><EmbeddedResource Include="Migrations/0001-initial.sql" LogicalName="DataPitcher.Infrastructure.Migrations.0001-initial.sql" /></ItemGroup>
```

```sql
-- Migrations/0001-initial.sql
CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL PRIMARY KEY, AppliedUtc TEXT NOT NULL);
CREATE TABLE Jobs (JobId TEXT NOT NULL PRIMARY KEY, RunId TEXT NOT NULL UNIQUE, PlanId TEXT NOT NULL, IdempotencyKey TEXT NOT NULL UNIQUE, State TEXT NOT NULL, CreatedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);
CREATE TABLE JobStateTransitions (TransitionId TEXT NOT NULL PRIMARY KEY, JobId TEXT NOT NULL REFERENCES Jobs(JobId), FromState TEXT NOT NULL, ToState TEXT NOT NULL, OccurredUtc TEXT NOT NULL);
CREATE TABLE JobLeases (JobId TEXT NOT NULL PRIMARY KEY REFERENCES Jobs(JobId), OwnerId TEXT NULL, ExpiresUtc TEXT NULL, FenceToken INTEGER NOT NULL);
CREATE TABLE BatchCheckpointMirrors (JobId TEXT NOT NULL REFERENCES Jobs(JobId), RunId TEXT NOT NULL, LastCommittedBatchSequence INTEGER NOT NULL, LastCommittedStableKey TEXT NULL, CumulativeRowCount INTEGER NOT NULL, SealedManifestHash TEXT NOT NULL, FenceToken INTEGER NOT NULL, UpdatedUtc TEXT NOT NULL, PRIMARY KEY (JobId, RunId));
```

```csharp
// Persistence/ControlRows.cs
using LinqToDB.Mapping;

namespace DataPitcher.Infrastructure.Persistence;

[Table("Jobs")] internal sealed class JobRow { [PrimaryKey, Column("JobId")] public string JobId { get; set; } = ""; [Column("RunId")] public string RunId { get; set; } = ""; [Column("PlanId")] public string PlanId { get; set; } = ""; [Column("IdempotencyKey")] public string IdempotencyKey { get; set; } = ""; [Column("State")] public string State { get; set; } = ""; [Column("CreatedUtc")] public string CreatedUtc { get; set; } = ""; [Column("UpdatedUtc")] public string UpdatedUtc { get; set; } = ""; }
[Table("JobStateTransitions")] internal sealed class JobStateTransitionRow { [PrimaryKey, Column("TransitionId")] public string TransitionId { get; set; } = ""; [Column("JobId")] public string JobId { get; set; } = ""; [Column("FromState")] public string FromState { get; set; } = ""; [Column("ToState")] public string ToState { get; set; } = ""; [Column("OccurredUtc")] public string OccurredUtc { get; set; } = ""; }
[Table("JobLeases")] internal sealed class JobLeaseRow { [PrimaryKey, Column("JobId")] public string JobId { get; set; } = ""; [Column("OwnerId")] public string? OwnerId { get; set; } [Column("ExpiresUtc")] public string? ExpiresUtc { get; set; } [Column("FenceToken")] public long FenceToken { get; set; } }

// Migrations/ControlDatabaseMigrator.cs
using LinqToDB.Data;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;

namespace DataPitcher.Infrastructure.Migrations;

public sealed class ControlDatabaseMigrator(ControlDatabase database, IClock clock)
{
    private static readonly (int Version, string Resource)[] Scripts = [(1, "DataPitcher.Infrastructure.Migrations.0001-initial.sql")];
    public void Apply()
    {
        using var db = database.Open();
        db.Execute("CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL PRIMARY KEY, AppliedUtc TEXT NOT NULL);");
        var applied = db.QueryToList<int>("SELECT Version FROM SchemaVersion").ToHashSet();
        foreach (var script in Scripts.Where(script => !applied.Contains(script.Version)))
        {
            using var transaction = db.BeginTransaction();
            db.Execute(ReadScript(script.Resource));
            db.Execute("INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES (@version, @appliedUtc)", new DataParameter("version", script.Version), new DataParameter("appliedUtc", clock.UtcNow.ToString("O")));
            transaction.Commit();
        }
    }
    private static string ReadScript(string resource)
    {
        using var stream = typeof(ControlDatabaseMigrator).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Missing migration resource: {resource}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 4: Run the focused test and confirm versioned migration success.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~ControlDatabaseMigratorTests"`

Expected: `Passed: 1. Failed: 0.` and the second call adds no second version row.

- [ ] **Step 5: Commit explicit schema migration support.**

Run: `git add src/DataPitcher.Infrastructure tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseFixture.cs tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseMigratorTests.cs && git commit -m "feat: add versioned SQLite control schema"`

### Task 4: Conditional leases and deterministic renewal

**Files:**
- Create: `src/DataPitcher.Infrastructure/Leasing/LeaseStore.cs`, `tests/DataPitcher.UnitTests/Infrastructure/LeaseStoreTests.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Infrastructure/LeaseStoreTests.cs`

- [ ] **Step 1: Write failing tests for exclusive acquisition, fence increment, and renewal without waiting.**

```csharp
using DataPitcher.Infrastructure.Leasing;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class LeaseStoreTests
{
    [Fact]
    public void LeaseStore_WhenExpiredOwnerIsReplaced_IncrementsTheMonotonicFence()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var jobId = fixture.SeedJob();
        var store = new LeaseStore(fixture.Database, fixture.Clock); var ttl = TimeSpan.FromMinutes(1);
        var first = store.Acquire(jobId, "worker-a", ttl); Assert.NotNull(first);
        Assert.Null(store.Acquire(jobId, "worker-b", ttl));
        fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1)));
        var second = store.Acquire(jobId, "worker-b", ttl); Assert.NotNull(second);
        Assert.Equal(first!.FenceToken + 1, second!.FenceToken);
    }

    [Fact]
    public void LeaseStore_WhenRenewalIsDue_RenewsAtTwoThirdsOfTtlWithoutSleeping()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var jobId = fixture.SeedJob();
        var store = new LeaseStore(fixture.Database, fixture.Clock); var ttl = TimeSpan.FromSeconds(60);
        var lease = store.Acquire(jobId, "worker-a", ttl)!;
        fixture.Clock.Advance(TimeSpan.FromSeconds(39)); Assert.Equal(lease, store.RenewIfDue(lease, ttl));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1)); var renewed = store.RenewIfDue(lease, ttl);
        Assert.NotNull(renewed); Assert.Equal(lease.FenceToken, renewed!.FenceToken); Assert.True(renewed.ExpiresUtc > lease.ExpiresUtc);
    }
}
```

- [ ] **Step 2: Run the focused test and confirm the missing lease-store failure.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~LeaseStoreTests"`

Expected: compilation fails with `CS0246` because `LeaseStore` does not exist.

- [ ] **Step 3: Implement conditional acquisition and owner-and-token guarded renewal.**

```csharp
using System.Globalization;
using LinqToDB.Data;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;

namespace DataPitcher.Infrastructure.Leasing;

public sealed class LeaseStore(ControlDatabase database, IClock clock)
{
    public LeaseGrant? Acquire(Guid jobId, string ownerId, TimeSpan ttl)
    {
        var now = clock.UtcNow; var expires = now.Add(ttl); Validate(ownerId, ttl);
        using var db = database.Open();
        var affected = db.Execute("UPDATE JobLeases SET OwnerId = @ownerId, ExpiresUtc = @expiresUtc, FenceToken = FenceToken + 1 WHERE JobId = @jobId AND (OwnerId IS NULL OR ExpiresUtc <= @nowUtc)", Parameters(jobId, ownerId, null, now, expires));
        return affected == 1 ? ReadGrant(db, jobId, ownerId, ttl, now) : null;
    }

    public LeaseGrant? RenewIfDue(LeaseGrant lease, TimeSpan ttl)
    {
        Validate(lease.OwnerId, ttl); var now = clock.UtcNow;
        if (now < lease.RenewAfterUtc) return lease;
        using var db = database.Open(); var expires = now.Add(ttl);
        var affected = db.Execute("UPDATE JobLeases SET ExpiresUtc = @expiresUtc WHERE JobId = @jobId AND OwnerId = @ownerId AND FenceToken = @fenceToken AND ExpiresUtc > @nowUtc", Parameters(lease.JobId, lease.OwnerId, lease.FenceToken, now, expires));
        return affected == 1 ? new LeaseGrant(lease.JobId, lease.OwnerId, lease.FenceToken, expires, RenewAfter(now, ttl)) : null;
    }

    private static DataParameter[] Parameters(Guid jobId, string ownerId, long? fenceToken, DateTimeOffset now, DateTimeOffset expires) => [new("jobId", jobId.ToString()), new("ownerId", ownerId), new("fenceToken", fenceToken), new("nowUtc", Stamp(now)), new("expiresUtc", Stamp(expires))];
    private static LeaseGrant ReadGrant(LinqToDB.Data.DataConnection db, Guid jobId, string ownerId, TimeSpan ttl, DateTimeOffset now) { var row = db.GetTable<JobLeaseRow>().Single(row => row.JobId == jobId.ToString()); var expires = DateTimeOffset.Parse(row.ExpiresUtc!, CultureInfo.InvariantCulture); return new(jobId, ownerId, row.FenceToken, expires, RenewAfter(now, ttl)); }
    private static DateTimeOffset RenewAfter(DateTimeOffset now, TimeSpan ttl) => now.AddTicks(ttl.Ticks * 2 / 3);
    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static void Validate(string ownerId, TimeSpan ttl) { if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("Lease owner is required.", nameof(ownerId)); if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl)); }
}
```

- [ ] **Step 4: Run the focused test and confirm deterministic lease behavior.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~LeaseStoreTests"`

Expected: `Passed: 2. Failed: 0.`; no test contains `Thread.Sleep`, `Task.Delay`, or a timing race.

- [ ] **Step 5: Commit the lease and fence-token protocol.**

Run: `git add src/DataPitcher.Infrastructure/Leasing/LeaseStore.cs tests/DataPitcher.UnitTests/Infrastructure/LeaseStoreTests.cs && git commit -m "feat: add fenced SQLite job leases"`

### Task 5: Idempotent starts and lease-fenced persisted transitions

**Files:**
- Create: `src/DataPitcher.Infrastructure/Persistence/JobStore.cs`, `tests/DataPitcher.UnitTests/Infrastructure/JobStoreTests.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Infrastructure/JobStoreTests.cs`

- [ ] **Step 1: Write failing tests for one start per idempotency key, transition history, and the stale-worker barrier.**

```csharp
using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Persistence;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class JobStoreTests
{
    [Fact]
    public void JobStore_WhenStartIsDuplicated_ReturnsTheOriginalQueuedJob()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock);
        var first = store.Start(new(Guid.NewGuid(), "start-42")); var duplicate = store.Start(new(Guid.NewGuid(), "start-42"));
        Assert.True(first.Created); Assert.False(duplicate.Created); Assert.Equal(first.Job.JobId, duplicate.Job.JobId); Assert.Equal(JobState.Queued, first.Job.State);
    }

    [Fact]
    public void JobStore_WhenLeaseIsCurrent_PersistsTheStateChangeAndItsHistory()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "start-43")).Job; var lease = new LeaseStore(fixture.Database, fixture.Clock).Acquire(job.JobId, "worker-a", TimeSpan.FromMinutes(1))!;
        var result = store.TryTransition(lease, JobState.Preparing);
        Assert.Equal(1, result.RowsAffected); Assert.Equal(JobState.Preparing, result.Job!.State);
        Assert.Equal([(JobState.Draft, JobState.Queued), (JobState.Queued, JobState.Preparing)], store.GetHistory(job.JobId));
    }

    [Fact]
    public async Task JobStore_WhenFirstOwnerIsStale_ItsGuardedWriteAffectsZeroRows()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new(Guid.NewGuid(), "start-44")).Job; var leases = new LeaseStore(fixture.Database, fixture.Clock); var ttl = TimeSpan.FromMinutes(1);
        var first = leases.Acquire(job.JobId, "worker-a", ttl)!; var firstReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var takeoverComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleWrite = Task.Run(async () => { firstReady.SetResult(true); await takeoverComplete.Task; return store.TryTransition(first, JobState.Preparing); });
        await firstReady.Task; fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1))); var second = leases.Acquire(job.JobId, "worker-b", ttl)!; takeoverComplete.SetResult(true);
        var stale = await staleWrite;
        Assert.True(second.FenceToken > first.FenceToken); Assert.Equal(0, stale.RowsAffected); Assert.DoesNotContain(store.GetHistory(job.JobId), x => x == (JobState.Queued, JobState.Preparing));
    }
}
```

- [ ] **Step 2: Run the focused test and confirm the missing job-store failure.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~JobStoreTests"`

Expected: compilation fails with `CS0246` because `JobStore` and `StartJobRequest` are not defined.

- [ ] **Step 3: Implement atomic idempotent creation and a commit-time lease fence on worker state writes.**

```csharp
using System.Globalization;
using LinqToDB;
using LinqToDB.Data;
using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;

namespace DataPitcher.Infrastructure.Persistence;

public sealed record TransferJob(Guid JobId, Guid RunId, Guid PlanId, string IdempotencyKey, JobState State);
public sealed record StartJobRequest(Guid PlanId, string IdempotencyKey);
public sealed record StartJobResult(TransferJob Job, bool Created);
public sealed record JobTransitionResult(TransferJob? Job, int RowsAffected);

public sealed class JobStore(ControlDatabase database, IClock clock)
{
    public StartJobResult Start(StartJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("Idempotency key is required.", nameof(request));
        using var db = database.Open(); using var transaction = db.BeginTransaction();
        var existing = db.GetTable<JobRow>().SingleOrDefault(row => row.IdempotencyKey == request.IdempotencyKey);
        if (existing is not null) return new(ToJob(existing), false);
        var now = Stamp(clock.UtcNow); var row = new JobRow { JobId = Guid.NewGuid().ToString(), RunId = Guid.NewGuid().ToString(), PlanId = request.PlanId.ToString(), IdempotencyKey = request.IdempotencyKey, State = JobState.Draft.ToString(), CreatedUtc = now, UpdatedUtc = now };
        db.Insert(row); PersistTransition(db, row, JobState.Draft, JobState.Queued, now); db.Insert(new JobLeaseRow { JobId = row.JobId, FenceToken = 0 }); transaction.Commit();
        return new(ToJob(row), true);
    }

    public JobTransitionResult TryTransition(LeaseGrant lease, JobState to)
    {
        using var db = database.Open(); using var transaction = db.BeginTransaction(); var row = db.GetTable<JobRow>().Single(row => row.JobId == lease.JobId.ToString()); var from = Enum.Parse<JobState>(row.State); JobStateMachine.EnsureTransition(from, to); var now = Stamp(clock.UtcNow);
        var affected = db.Execute("UPDATE Jobs SET State = @toState, UpdatedUtc = @nowUtc WHERE JobId = @jobId AND State = @fromState AND EXISTS (SELECT 1 FROM JobLeases WHERE JobId = @jobId AND OwnerId = @ownerId AND FenceToken = @fenceToken AND ExpiresUtc > @nowUtc)", new DataParameter("toState", to.ToString()), new DataParameter("nowUtc", now), new DataParameter("jobId", lease.JobId.ToString()), new DataParameter("fromState", from.ToString()), new DataParameter("ownerId", lease.OwnerId), new DataParameter("fenceToken", lease.FenceToken));
        if (affected == 0) return new(null, 0);
        PersistHistory(db, lease.JobId, from, to, now); row.State = to.ToString(); row.UpdatedUtc = now; transaction.Commit(); return new(ToJob(row), 1);
    }

    public TransferJob Get(Guid jobId) => ToJob(database.Open().GetTable<JobRow>().Single(row => row.JobId == jobId.ToString()));
    public IReadOnlyList<(JobState From, JobState To)> GetHistory(Guid jobId) => database.Open().GetTable<JobStateTransitionRow>().Where(row => row.JobId == jobId.ToString()).OrderBy(row => row.OccurredUtc).Select(row => new { row.FromState, row.ToState }).AsEnumerable().Select(row => (Enum.Parse<JobState>(row.FromState), Enum.Parse<JobState>(row.ToState))).ToArray();
    private static void PersistTransition(LinqToDB.Data.DataConnection db, JobRow row, JobState from, JobState to, string now) { db.Execute("UPDATE Jobs SET State = @toState, UpdatedUtc = @nowUtc WHERE JobId = @jobId AND State = @fromState", new DataParameter("toState", to.ToString()), new DataParameter("nowUtc", now), new DataParameter("jobId", row.JobId), new DataParameter("fromState", from.ToString())); row.State = to.ToString(); row.UpdatedUtc = now; PersistHistory(db, Guid.Parse(row.JobId), from, to, now); }
    private static void PersistHistory(LinqToDB.Data.DataConnection db, Guid jobId, JobState from, JobState to, string now) => db.Insert(new JobStateTransitionRow { TransitionId = Guid.NewGuid().ToString(), JobId = jobId.ToString(), FromState = from.ToString(), ToState = to.ToString(), OccurredUtc = now });
    private static TransferJob ToJob(JobRow row) => new(Guid.Parse(row.JobId), Guid.Parse(row.RunId), Guid.Parse(row.PlanId), row.IdempotencyKey, Enum.Parse<JobState>(row.State));
    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
```

The `UPDATE ... EXISTS (SELECT 1 FROM JobLeases ...)` predicate is the control-store fencing point: SQLite evaluates the owner, unexpired lease, and exact monotonic token while executing the transaction’s write. No state-history row is inserted when the guarded update reports zero affected rows. The transfer slice must apply the same condition to the **target** checkpoint update, where it prevents stale target commits.

- [ ] **Step 4: Run the focused test and confirm idempotency and stale-fence protection.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~JobStoreTests"`

Expected: `Passed: 3. Failed: 0.`; the stale-owner assertion reports zero rows affected without a delay or scheduler race.

- [ ] **Step 5: Commit durable job orchestration.**

Run: `git add src/DataPitcher.Infrastructure/Persistence/JobStore.cs tests/DataPitcher.UnitTests/Infrastructure/JobStoreTests.cs && git commit -m "feat: persist idempotent fenced job transitions"`

### Task 6: Derived checkpoint mirror boundary and full-slice evidence

**Files:**
- Create: `src/DataPitcher.Infrastructure/Checkpoints/CheckpointMirrorStore.cs`, `tests/DataPitcher.UnitTests/Infrastructure/CheckpointMirrorStoreTests.cs`, `tests/DataPitcher.ArchitectureTests/CheckpointMirrorBoundaryTests.cs`
- Modify: `tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj`, `docs/test-coverage-matrix.md`
- Test: `tests/DataPitcher.UnitTests/Infrastructure/CheckpointMirrorStoreTests.cs`, `tests/DataPitcher.ArchitectureTests/CheckpointMirrorBoundaryTests.cs`

- [ ] **Step 1: Write the failing write-only mirror and architecture-boundary tests.**

```xml
<!-- append to tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj -->
<ItemGroup><ProjectReference Include="../../src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj" /></ItemGroup>
```

```csharp
// CheckpointMirrorStoreTests.cs
using DataPitcher.Infrastructure.Checkpoints;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class CheckpointMirrorStoreTests
{
    [Fact]
    public void CheckpointMirrorStore_WhenUpdated_ReplacesTheDerivedMirrorForOneJobRun()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var jobId = fixture.SeedJob(); var runId = Guid.NewGuid();
        var store = new CheckpointMirrorStore(fixture.Database); store.Upsert(new(jobId, runId, 4, "key-4", 40, "seal", 7, fixture.Clock.UtcNow)); store.Upsert(new(jobId, runId, 5, "key-5", 50, "seal", 7, fixture.Clock.UtcNow));
        using var db = fixture.Database.Open(); Assert.Equal([5L], db.QueryToList<long>($"SELECT LastCommittedBatchSequence FROM BatchCheckpointMirrors WHERE JobId = '{jobId}' AND RunId = '{runId}'"));
    }
}

// CheckpointMirrorBoundaryTests.cs
using System.Reflection;
using DataPitcher.Infrastructure.Checkpoints;
using Xunit;

namespace DataPitcher.ArchitectureTests;

public sealed class CheckpointMirrorBoundaryTests
{
    [Fact]
    public void CheckpointMirror_IsWritableOnlyAndNoOtherProductionFileAccessesItsTable()
    {
        var root = FindRoot(); var mirrorFile = Path.Combine(root, "src", "DataPitcher.Infrastructure", "Checkpoints", "CheckpointMirrorStore.cs");
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories), file => file != mirrorFile && File.ReadAllText(file).Contains("BatchCheckpointMirrors", StringComparison.Ordinal));
        Assert.Equal(["Upsert"], typeof(CheckpointMirrorStore).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Select(method => method.Name));
    }
    private static string FindRoot() { for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) if (File.Exists(Path.Combine(directory.FullName, "DataPitcher.sln"))) return directory.FullName; throw new DirectoryNotFoundException("DataPitcher.sln"); }
}
```

- [ ] **Step 2: Run the focused tests and confirm the missing mirror-store failure.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~CheckpointMirrorStoreTests" && dotnet test tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj --filter "FullyQualifiedName~CheckpointMirrorBoundaryTests"`

Expected: compilation fails with `CS0246` because `CheckpointMirrorStore` and `DerivedCheckpointMirror` do not exist.

- [ ] **Step 3: Implement the one-way mirror writer and update traceability.**

```csharp
using System.Globalization;
using LinqToDB.Data;
using DataPitcher.Infrastructure.Storage;

namespace DataPitcher.Infrastructure.Checkpoints;

public sealed record DerivedCheckpointMirror(Guid JobId, Guid RunId, long LastCommittedBatchSequence, string? LastCommittedStableKey, long CumulativeRowCount, string SealedManifestHash, long FenceToken, DateTimeOffset UpdatedUtc);

public sealed class CheckpointMirrorStore(ControlDatabase database)
{
    public void Upsert(DerivedCheckpointMirror mirror)
    {
        using var db = database.Open();
        db.Execute("INSERT INTO BatchCheckpointMirrors (JobId, RunId, LastCommittedBatchSequence, LastCommittedStableKey, CumulativeRowCount, SealedManifestHash, FenceToken, UpdatedUtc) VALUES (@jobId, @runId, @batch, @key, @rows, @seal, @fence, @updated) ON CONFLICT(JobId, RunId) DO UPDATE SET LastCommittedBatchSequence = excluded.LastCommittedBatchSequence, LastCommittedStableKey = excluded.LastCommittedStableKey, CumulativeRowCount = excluded.CumulativeRowCount, SealedManifestHash = excluded.SealedManifestHash, FenceToken = excluded.FenceToken, UpdatedUtc = excluded.UpdatedUtc", new DataParameter("jobId", mirror.JobId.ToString()), new DataParameter("runId", mirror.RunId.ToString()), new DataParameter("batch", mirror.LastCommittedBatchSequence), new DataParameter("key", mirror.LastCommittedStableKey), new DataParameter("rows", mirror.CumulativeRowCount), new DataParameter("seal", mirror.SealedManifestHash), new DataParameter("fence", mirror.FenceToken), new DataParameter("updated", mirror.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)));
    }
}
```

Add these exact rows under `## Plans, transfer, verification, recovery, and security` in `docs/test-coverage-matrix.md`:

```markdown
| DP-ORCH-001 | Starts are idempotent; state writes are persisted and reject a stale SQLite fence token. | `architecture.md` §§7-8; ADR 0001 §§2-3 | Slice 3 Tasks 4-5 | `LeaseStoreTests`, `JobStoreTests` | Planned |
| DP-ORCH-002 | SQLite checkpoint mirrors are derived, write-only control data and cannot be a correctness input. | ADR 0001 §§2, 4 | Slice 3 Task 6 | `CheckpointMirrorStoreTests`, `CheckpointMirrorBoundaryTests` | Planned |
```

Code review rule: reject any production method that reads `BatchCheckpointMirrors`, accepts `DerivedCheckpointMirror` as an execution/recovery input, or decides resume, replay, verification, or apply behavior from SQLite. Those decisions must read the provider-owned target checkpoint keyed by `(job_id, run_id)` and condition its update on the target fence token.

- [ ] **Step 4: Run all lanes and confirm the merged coverage gate.**

Run: `scripts/test-unit.sh && scripts/test-postgres.sh && scripts/test-all.sh`

Expected: the unit lane passes without Docker for this slice; the PostgreSQL lane passes its existing container tests; `test-all.sh` reports `Merged coverage: line=100% branch=100% method=100.00%` and exits zero. Do not lower or bypass the gate.

- [ ] **Step 5: Commit the checkpoint boundary and evidence.**

Run: `git add src/DataPitcher.Infrastructure/Checkpoints/CheckpointMirrorStore.cs tests/DataPitcher.UnitTests/Infrastructure/CheckpointMirrorStoreTests.cs tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj tests/DataPitcher.ArchitectureTests/CheckpointMirrorBoundaryTests.cs docs/test-coverage-matrix.md && git commit -m "feat: enforce derived checkpoint mirror boundary"`

## Self-Review

Covered: Infrastructure is the only SQLite control-database project; package versions are pinned; migrations are embedded versioned SQL with `SchemaVersion`; every listed state and legal/illegal transition is tested; starts are idempotent; leases conditionally acquire and monotonically fence; renewal uses an injectable clock at two-thirds TTL; the explicit stale-owner test advances a manual clock, acquires a replacement owner, and proves the first guarded write affects zero rows; and the target-checkpoint/mirror boundary has both a write-only API and an architecture test.

Deferred: target checkpoint table creation and target-transaction fencing, recovery and mutation-journal repair, transfer execution, hosted queue workers, API commands, authentication, and roadmap-numbering editorial reconciliation. SQLite tests require no Docker; the existing PostgreSQL lane remains separate.

Consistency checked: all later-task types are introduced earlier (`IClock` and `LeaseGrant` in Task 1; `JobState` in Task 2; row mappings and fixture in Task 3; `LeaseStore` in Task 4); later calls consistently use `Open`, `Apply`, `Acquire`, `RenewIfDue`, `Start`, `TryTransition`, `GetHistory`, and `Upsert` with the signatures defined in their creating task.
