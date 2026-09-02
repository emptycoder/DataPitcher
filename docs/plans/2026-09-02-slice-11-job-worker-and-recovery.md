# DataPitcher Slice 11: Job Worker and Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run sealed transfer jobs safely outside HTTP lifetimes, recover them deterministically after interruption, and preserve target-side fencing and database-state repair guarantees.

**Architecture:** `DataPitcher.Infrastructure` adds one hosted worker that coordinates injected source, target, and pipeline sessions. SQLite records scheduling, commands, transition history, and a derived checkpoint mirror; the target checkpoint and target-local mutation journal remain authoritative for commit, recovery, fencing, and repair under ADR 0001. Provider writers and the bounded data pipeline are deliberately not implemented here.

**Tech Stack:** .NET SDK 10.0.400, C# latest, Microsoft.Extensions.Hosting.Abstractions, SQLite in process, LINQ to DB 6.4.0, xUnit 2.9.3, Coverlet collector 6.0.4, ReportGenerator, Bash.

---

## File Structure

- `src/DataPitcher.Infrastructure/Worker/WorkerContracts.cs` — provider-independent job-run, checkpoint, owned-session, retry, and fault-injection contracts.
- `src/DataPitcher.Infrastructure/Worker/JobWorker.cs` — hosted claim, lease-renewal, run, pause, cancellation, retry, and terminal-transition coordinator.
- `src/DataPitcher.Infrastructure/Worker/LeaseRenewer.cs` — clock-derived lease renewal loop with an isolated SQLite connection per renewal.
- `src/DataPitcher.Infrastructure/Worker/RecoveryCoordinator.cs` — target-fence acquisition, checkpoint reconciliation, target mutation repair, quarantine, and resumption decisions.
- `src/DataPitcher.Infrastructure/Worker/WorkerDelay.cs` — injectable wait boundary used by lease renewal and bounded retry backoff.
- `src/DataPitcher.Infrastructure/Checkpoints/CheckpointMirrorStore.cs` — write-only SQLite projection of an authoritative target checkpoint.
- `src/DataPitcher.Infrastructure/Persistence/JobStore.cs` — asynchronous cancellable claim, command, recovery, failure-code, and persisted-transition operations.
- `src/DataPitcher.Infrastructure/Persistence/ControlRows.cs` — LINQ to DB mappings for recovery status and derived checkpoint rows.
- `src/DataPitcher.Infrastructure/Migrations/0002-job-recovery.sql` — recovery failure-code and command-state schema migration.
- `src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj` — embeds migration 0002 and references hosting abstractions.
- `src/DataPitcher.Core/Jobs/JobState.cs` — permits only explicit recovery requeue transitions from interrupted active states.
- `tests/DataPitcher.UnitTests/Worker/WorkerTestSupport.cs` — deterministic manual sessions, target journal, control queue, clock wait, and fault doubles.
- `tests/DataPitcher.UnitTests/Worker/WorkerContractsTests.cs` — contract-value and fault-point coverage.
- `tests/DataPitcher.UnitTests/Infrastructure/JobStoreRecoveryTests.cs` — SQLite idempotency, claim exclusivity, commands, recovery state, and mirror persistence.
- `tests/DataPitcher.UnitTests/Worker/JobWorkerTests.cs` — ordinary run, renewal, pause, resume, cancel, and SCC-unit behavior without Docker.
- `tests/DataPitcher.UnitTests/Worker/RecoveryCoordinatorTests.cs` — checkpoint authority, seal mismatch, repair, quarantine, and non-resumable recovery.
- `tests/DataPitcher.UnitTests/Worker/WorkerFaultAndFenceTests.cs` — deterministic fault, retry, interruption, unknown-commit, and stale-worker fencing proof.
- `tests/DataPitcher.ArchitectureTests/CheckpointMirrorBoundaryTests.cs` — keeps target checkpoints authoritative and prevents production reads of the SQLite mirror.
- `docs/test-coverage-matrix.md` — traces worker and recovery evidence to ADR 0001 and architecture sections 8–9.
- `docs/plans/2026-09-02-slice-11-job-worker-and-recovery.md` — this implementation plan.

## Scope and Deferrals

This slice owns orchestration and recovery, not provider writers, native bulk-copy code, target DDL, source SQL, or the bounded reader/converter/batch/writer pipeline. It consumes the contracts below. A later provider/pipeline slice implements `ITransferReadSession` with sequential source access and a bounded queue of one or two batches, and implements `ITargetRunSession.ApplyAsync` by staging, applying, conditionally advancing the target checkpoint, and committing in one target transaction. The worker must not receive an `HttpContext`, request service scope, request connection, or request cancellation token as its lifetime; application shutdown is its host cancellation token.

Every worker-owned session is newly opened for that worker and disposed by it. A source reader and source connection belong only to `ITransferReadSession`; a target connection and transaction belong only to `ITargetRunSession`; the renewal loop calls `LeaseStore`, which opens a separate SQLite connection. No mutable connection, transaction, reader, or command is shared concurrently. Lease expiration is scheduling information, not target-write authorization. Each target apply asserts the immutable `LeaseGrant.FenceToken` against the target checkpoint in the same transaction as its staged apply and checkpoint update. A worker that wakes after losing its lease is therefore harmless: it can fail, but it cannot commit a stale target mutation.

Pause is a boundary command. On observing `Pausing`, the worker lets the current normal batch—or the current atomic SCC transfer unit—finish its target transaction and checkpoint update. It then stops the pipeline from admitting another unit, discards any prefetched but uncommitted in-memory rows, disposes the source reader and source connection, and removes target staging rows for the completed `(job_id, fence_token, batch_sequence)` in that same completed apply transaction. No payload is durable across a pause. Resume opens fresh sessions and keyset-seeks strictly after the target checkpoint’s last committed `StableKey`; it never uses `OFFSET`. A cancellation command cancels the linked token supplied to every source, target, target-repair, mirror, and SQLite operation, aborts an incomplete unit, disposes both sessions, and reaches `Cancelled` only after no uncommitted unit remains.

An SCC selected for an atomic cycle strategy is one `TransferUnit` regardless of row count. It is not pausable between its member rows or internal writer batches. A pause requested while that unit is applying becomes effective only before it begins or after its one target commit. A connection loss or process interruption during it rolls back the entire target transaction; its checkpoint does not advance and recovery retries the complete SCC unit.

Restart recovery never uses `BatchCheckpointMirrors` to decide whether a batch committed. For every acquired interrupted job, it first moves the target fence forward, reads the target checkpoint, rejects a differing `ManifestSealHash`, repairs target-local mutations, and overwrites the SQLite mirror from the target checkpoint. A target batch committed if and only if this checkpoint advanced. An interrupted run that lacks durable source/target resume capability is transitioned to `Failed` with failure code `NonResumableInterrupted`; it is never replayed by guesswork. A target-recovery failure is likewise a persisted failure with its classified code, not a new run.

Target-local mutation recovery distinguishes durable state from connection-scoped state. A committed SQL Server `ALTER TABLE ... NOCHECK CONSTRAINT` (disabled and untrusted constraints) and `DISABLE TRIGGER` survive a crash; PostgreSQL committed `ALTER TABLE ... DISABLE TRIGGER`, including its foreign-key trigger effect, and committed constraint validation-state changes survive. They need a target-local journal record in the same target transaction as the mutation and require catalog detection followed by repair and verification. SQL Server `SET IDENTITY_INSERT table ON`, PostgreSQL `SET CONSTRAINTS ... DEFERRED`, `SET LOCAL`, open transactions, temporary tables, session settings, readers, and uncommitted DDL die with their owning connection or transaction. The worker closes its dedicated target connection on every pause, cancellation, fault, and recovery boundary rather than attempting to repair those dead settings. If a surviving mutation cannot be restored and verified, the journal entry is marked `Quarantined`, the target table is quarantined, and no automatic clear or retry is permitted.

All tests for pure coordination use the in-process SQLite fixture and deterministic fakes; they need no Docker. Provider database tests that later implement the contracts must prove the actual target conditional-checkpoint update, catalog repair, and DDL behavior. Warnings remain errors; xUnit analyzer diagnostics are build failures. Use `Assert.Single(collection, predicate)` and `Assert.DoesNotContain(collection, predicate)`, not analyzer-violating assertion shapes. Each public member introduced below receives an observable test in its creating task. `scripts/test-all.sh` is the only merged line, branch, and method coverage enforcement point, and must report 100 percent for all three measures.

### Task 1: Define worker, recovery, and deterministic-fault contracts

**Files:**
- Create: `src/DataPitcher.Infrastructure/Worker/WorkerContracts.cs`, `src/DataPitcher.Infrastructure/Worker/WorkerDelay.cs`, `tests/DataPitcher.UnitTests/Worker/WorkerContractsTests.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Worker/WorkerContractsTests.cs`

1. - [ ] **Write the failing contract tests with complete public-member coverage.** Create a `TransferRun` with a stable key checkpoint, verify its immutable values, create each `TransferFaultPoint` with `Enum.GetValues<TransferFaultPoint>()`, and assert that an `AtomicComponent` transfer unit reports `CanPauseAfterCommit` while a regular unit does. Add a scripted `IWorkerFaults` test that releases its configured point exactly once and then succeeds.

```csharp
using DataPitcher.Core.Identity;
using DataPitcher.Infrastructure.Worker;
using Xunit;

namespace DataPitcher.UnitTests.Worker;

public sealed class WorkerContractsTests
{
    [Fact]
    public async Task Faults_WhenPointIsConfigured_ThrowsOnlyForThatPoint()
    {
        var faults = new ScriptedWorkerFaults(TransferFaultPoint.BeforeTargetCommit);
        await Assert.ThrowsAsync<SimulatedWorkerFaultException>(() => faults.HitAsync(TransferFaultPoint.BeforeTargetCommit, CancellationToken.None));
        await faults.HitAsync(TransferFaultPoint.BeforeTargetCommit, CancellationToken.None);
        Assert.Contains(TransferFaultPoint.PermanentFailure, Enum.GetValues<TransferFaultPoint>());
    }

    [Fact]
    public void TransferUnit_WhenAtomicComponent_IsPausableOnlyAfterItsCommit()
    {
        var unit = new TransferUnit(4, new StableKey([new KeyComponent("Id", 9)]), 3, TransferUnitKind.AtomicComponent);
        Assert.True(unit.CanPauseAfterCommit);
        Assert.Equal(4, unit.BatchSequence);
        Assert.Equal(3, unit.RowCount);
    }

    private sealed class ScriptedWorkerFaults(TransferFaultPoint point) : IWorkerFaults
    {
        private bool _pending = true;
        public Task HitAsync(TransferFaultPoint current, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pending && current == point) { _pending = false; throw new SimulatedWorkerFaultException(point); }
            return Task.CompletedTask;
        }
    }
}
```

2. - [ ] **Run the focused test and confirm the contract namespace is absent.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~WorkerContractsTests"`

Expected: compilation fails with `CS0234` because `DataPitcher.Infrastructure.Worker` does not exist.

3. - [ ] **Create the complete contracts and an injectable delay boundary.** These types deliberately carry no provider connection or row payload. `ApplyAsync` is the future bounded-pipeline writer seam and must make target staging, business apply, checkpoint advance, and fence assertion one transaction.

```csharp
// src/DataPitcher.Infrastructure/Worker/WorkerContracts.cs
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Persistence;

namespace DataPitcher.Infrastructure.Worker;

public sealed record TransferRun(Guid JobId, Guid RunId, string ManifestSealHash, bool SupportsDurableResume);
public sealed record TargetCheckpoint(Guid JobId, Guid RunId, long BatchSequence, StableKey? LastStableKey, long RowCount, string ManifestSealHash, long FenceToken);
public enum TransferUnitKind { Batch, AtomicComponent }
public sealed record TransferUnit(long BatchSequence, StableKey LastStableKey, long RowCount, TransferUnitKind Kind)
{
    public bool CanPauseAfterCommit => Kind is TransferUnitKind.Batch or TransferUnitKind.AtomicComponent;
}
public enum TargetMutationKind { DisabledConstraint, UntrustedConstraint, DisabledTrigger }
public enum MutationJournalState { PendingRepair, Repaired, Quarantined }
public sealed record TargetMutation(string Table, string ObjectName, TargetMutationKind Kind);
public sealed record MutationJournalEntry(Guid EntryId, TargetMutation Mutation, MutationJournalState State, string? Detail);
public sealed record RecoverySnapshot(TargetCheckpoint Checkpoint, IReadOnlyList<MutationJournalEntry> Mutations);
public enum CommitDisposition { NotCommitted, Unknown }
public sealed class TransferAttemptException(CommitDisposition disposition, Exception innerException) : Exception(innerException.Message, innerException)
{
    public CommitDisposition Disposition { get; } = disposition;
}
public sealed class TargetFenceLostException : InvalidOperationException { public TargetFenceLostException() : base("Target fence token is no longer current.") { } }
public sealed class ManifestSealMismatchException : InvalidOperationException { public ManifestSealMismatchException() : base("Target checkpoint manifest seal hash does not match the sealed transfer run.") { } }
public sealed class NonResumableInterruptedException : InvalidOperationException { public NonResumableInterruptedException() : base("Interrupted run does not support durable resume.") { } }
public sealed class SimulatedWorkerFaultException(TransferFaultPoint point) : Exception($"Simulated worker fault: {point}.") { public TransferFaultPoint Point { get; } = point; }
public enum TransferFaultPoint { BeforeTargetCommit, DuringTargetWrite, AfterTargetCommitBeforeControlMirror, ProcessInterrupted, TargetConnectionLost, Cancellation, CommandTimeout, TransientThenSuccess, PermanentFailure, RecoveryFailure }
public interface IWorkerFaults { Task HitAsync(TransferFaultPoint point, CancellationToken cancellationToken); }
public interface IJobRunCatalog { Task<TransferRun> LoadAsync(TransferJob job, CancellationToken cancellationToken); }
public interface ITransferReadSession : IAsyncDisposable
{
    Task<TransferUnit?> ReadNextAsync(CancellationToken cancellationToken);
    Task DiscardUncommittedAsync(CancellationToken cancellationToken);
}
public interface ITransferReadSessionFactory { Task<ITransferReadSession> OpenKeysetAsync(TransferRun run, StableKey? startAfter, CancellationToken cancellationToken); }
public interface ITargetRunSession : IAsyncDisposable
{
    Task<RecoverySnapshot> AcquireFenceReadCheckpointAndJournalAsync(TransferRun run, LeaseGrant lease, CancellationToken cancellationToken);
    Task<IReadOnlyList<MutationJournalEntry>> RepairMutationsAsync(IReadOnlyList<MutationJournalEntry> mutations, CancellationToken cancellationToken);
    Task QuarantineAsync(TargetMutation mutation, string reason, CancellationToken cancellationToken);
    Task<TargetCheckpoint> ApplyAsync(TransferRun run, LeaseGrant lease, TransferUnit unit, CancellationToken cancellationToken);
    Task DiscardUncommittedAsync(CancellationToken cancellationToken);
}
public interface ITargetRunSessionFactory { Task<ITargetRunSession> OpenAsync(TransferRun run, CancellationToken cancellationToken); }
public interface IControlCheckpointMirror { Task OverwriteAsync(TargetCheckpoint checkpoint, CancellationToken cancellationToken); }
public sealed record JobClaim(TransferJob Job, LeaseGrant Lease, bool IsInterrupted);
public interface IJobControl
{
    Task<JobClaim?> TryClaimNextAsync(string ownerId, TimeSpan leaseTtl, CancellationToken cancellationToken);
    Task<JobState> GetStateAsync(Guid jobId, CancellationToken cancellationToken);
    Task RequestPauseAsync(Guid jobId, CancellationToken cancellationToken);
    Task RequestResumeAsync(Guid jobId, CancellationToken cancellationToken);
    Task RequestCancelAsync(Guid jobId, CancellationToken cancellationToken);
    Task PrepareAsync(JobClaim claim, CancellationToken cancellationToken);
    Task MarkRunningAsync(LeaseGrant lease, CancellationToken cancellationToken);
    Task MarkPausedAsync(LeaseGrant lease, CancellationToken cancellationToken);
    Task MarkCancelledAsync(LeaseGrant lease, CancellationToken cancellationToken);
    Task MarkVerifyingAsync(LeaseGrant lease, CancellationToken cancellationToken);
    Task MarkFailedAsync(LeaseGrant lease, string failureCode, CancellationToken cancellationToken);
}

// src/DataPitcher.Infrastructure/Worker/WorkerDelay.cs
using DataPitcher.Infrastructure.Time;

namespace DataPitcher.Infrastructure.Worker;

public interface IWorkerDelay { Task UntilAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken); }
public sealed class ClockWorkerDelay(IClock clock) : IWorkerDelay
{
    public Task UntilAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Delay(dueUtc <= clock.UtcNow ? TimeSpan.Zero : dueUtc - clock.UtcNow, cancellationToken);
    }
}
```

4. - [ ] **Run the focused contract test and confirm it passes.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~WorkerContractsTests"`

Expected: `Passed: 2. Failed: 0.` after adding the local scripted fault double used by the test; every public contract property and both delay branches are exercised.

5. - [ ] **Commit the contract seam.**

Run: `git add src/DataPitcher.Infrastructure/Worker/WorkerContracts.cs src/DataPitcher.Infrastructure/Worker/WorkerDelay.cs tests/DataPitcher.UnitTests/Worker/WorkerContractsTests.cs && git commit -m "feat: define worker recovery contracts"`

### Task 2: Persist exclusive claims, commands, recovery failures, and target-derived mirrors

**Files:**
- Create: `src/DataPitcher.Infrastructure/Checkpoints/CheckpointMirrorStore.cs`, `src/DataPitcher.Infrastructure/Migrations/0002-job-recovery.sql`, `tests/DataPitcher.UnitTests/Infrastructure/JobStoreRecoveryTests.cs`
- Modify: `src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj`, `src/DataPitcher.Infrastructure/Migrations/ControlDatabaseMigrator.cs`, `src/DataPitcher.Infrastructure/Persistence/ControlRows.cs`, `src/DataPitcher.Infrastructure/Persistence/JobStore.cs`, `src/DataPitcher.Infrastructure/Leasing/LeaseStore.cs`, `src/DataPitcher.Core/Jobs/JobState.cs`
- Test: `tests/DataPitcher.UnitTests/Infrastructure/JobStoreRecoveryTests.cs`

1. - [ ] **Write the failing in-process SQLite tests.** Test two simultaneous duplicate `StartJobRequest` calls followed by two `TryClaimNextAsync` calls and assert only one `JobClaim` has the original job ID. Test an expired `Running` job becomes an interrupted claim only after the manual clock crosses its lease expiry. Test `RequestPauseAsync` records `Running -> Pausing`, `RequestCancelAsync` records `Paused -> Cancelling`, and `MarkFailedAsync` stores `NonResumableInterrupted`. Finally, overwrite the mirror twice and inspect SQLite directly to prove the later target checkpoint replaces the display copy.

2. - [ ] **Run the focused SQLite test and confirm the new asynchronous APIs are absent.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~JobStoreRecoveryTests"`

Expected: compilation fails with `CS1061` because `JobStore` does not yet define `TryClaimNextAsync` and `CheckpointMirrorStore` does not exist.

3. - [ ] **Add migration 0002 and implement the complete cancellable control-side path.** Embed the migration beside 0001, list `(2, "DataPitcher.Infrastructure.Migrations.0002-job-recovery.sql")` in `ControlDatabaseMigrator.Scripts`, map every column with `[Column]`, and use LINQ to DB asynchronous command/query APIs with the passed cancellation token—never an uncancellable synchronous wrapper. Add `FailureCode TEXT NULL` to `Jobs`; do not add a SQLite checkpoint reader.

```sql
-- src/DataPitcher.Infrastructure/Migrations/0002-job-recovery.sql
ALTER TABLE Jobs ADD COLUMN FailureCode TEXT NULL;
CREATE INDEX IF NOT EXISTS IX_Jobs_Recovery ON Jobs(State, UpdatedUtc);
```

```csharp
// additions to src/DataPitcher.Core/Jobs/JobState.cs
// These are recovery requeues, not normal execution transitions.
JobState.Preparing => [JobState.Running, JobState.Pausing, JobState.Cancelling, JobState.Failed, JobState.Queued],
JobState.Running => [JobState.Pausing, JobState.Cancelling, JobState.Verifying, JobState.Failed, JobState.Queued],
JobState.Pausing => [JobState.Paused, JobState.Cancelling, JobState.Failed, JobState.Queued],
```

The recovery requeues deliberately expand the exhaustive state-machine transition set: an interrupted active job must return to `Queued` so restart recovery can reclaim it rather than leaving it permanently unrecoverable.

```csharp
// src/DataPitcher.Infrastructure/Checkpoints/CheckpointMirrorStore.cs
using System.Globalization;
using LinqToDB.Data;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Worker;

namespace DataPitcher.Infrastructure.Checkpoints;

public sealed class CheckpointMirrorStore(ControlDatabase database) : IControlCheckpointMirror
{
    public async Task OverwriteAsync(TargetCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        using var db = database.Open();
        await db.ExecuteAsync("INSERT INTO BatchCheckpointMirrors (JobId, RunId, LastCommittedBatchSequence, LastCommittedStableKey, CumulativeRowCount, SealedManifestHash, FenceToken, UpdatedUtc) VALUES (@job, @run, @batch, @key, @rows, @seal, @fence, @updated) ON CONFLICT(JobId, RunId) DO UPDATE SET LastCommittedBatchSequence = excluded.LastCommittedBatchSequence, LastCommittedStableKey = excluded.LastCommittedStableKey, CumulativeRowCount = excluded.CumulativeRowCount, SealedManifestHash = excluded.SealedManifestHash, FenceToken = excluded.FenceToken, UpdatedUtc = excluded.UpdatedUtc", new DataParameter("job", checkpoint.JobId.ToString()), new DataParameter("run", checkpoint.RunId.ToString()), new DataParameter("batch", checkpoint.BatchSequence), new DataParameter("key", checkpoint.LastStableKey?.ToString()), new DataParameter("rows", checkpoint.RowCount), new DataParameter("seal", checkpoint.ManifestSealHash), new DataParameter("fence", checkpoint.FenceToken), new DataParameter("updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)), cancellationToken);
    }
}
```

Implement `JobStore : IJobControl` and `LeaseStore` asynchronous APIs with one conditional SQLite transaction per transition: candidate query is only scheduling discovery; `LeaseStore.AcquireAsync` remains the decisive claim; every state update includes `OwnerId`, unexpired `ExpiresUtc`, and exact `FenceToken`; the history row and `FailureCode` update share that transaction. `TryClaimNextAsync` considers `Queued` plus expired `Preparing`, `Running`, and `Pausing` rows, labels the latter `IsInterrupted`, and does not claim another worker’s unexpired lease. `PrepareAsync` first records interrupted active state to `Queued`, then `Queued -> Preparing`; normal jobs only record the latter. Add separately tested `RequestPauseAsync`, `RequestResumeAsync`, and `RequestCancelAsync` conditional command methods, each with history and its cancellation token passed to SQLite.

4. - [ ] **Run the focused SQLite suite and confirm durable scheduling behavior.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~JobStoreRecoveryTests"`

Expected: `Passed: 5. Failed: 0.` The duplicate start has one `Jobs` row, exactly one claimant owns it, only expired active jobs become interrupted claims, and mirror inspection reports the target-derived second checkpoint.

5. - [ ] **Commit the control-store recovery support.**

Run: `git add src/DataPitcher.Core/Jobs/JobState.cs src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj src/DataPitcher.Infrastructure/Migrations src/DataPitcher.Infrastructure/Persistence src/DataPitcher.Infrastructure/Checkpoints tests/DataPitcher.UnitTests/Infrastructure/JobStoreRecoveryTests.cs && git commit -m "feat: persist recoverable worker jobs"`

### Task 3: Implement lease renewal and the hosted worker’s normal run

**Files:**
- Create: `src/DataPitcher.Infrastructure/Worker/LeaseRenewer.cs`, `src/DataPitcher.Infrastructure/Worker/JobWorker.cs`, `tests/DataPitcher.UnitTests/Worker/WorkerTestSupport.cs`, `tests/DataPitcher.UnitTests/Worker/JobWorkerTests.cs`
- Modify: `src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj`
- Test: `tests/DataPitcher.UnitTests/Worker/JobWorkerTests.cs`

1. - [ ] **Write failing ordinary-run and renewal tests with barriers, not delays.** The fake queue returns one queued claim; the fake target returns a checkpoint then a committed unit; assert persisted calls `Prepare`, `MarkRunning`, and target-derived mirror overwrite before completion. Configure a `GateWorkerDelay` to hold at `lease.RenewAfterUtc`, advance `ManualClock` through it, release the gate, and assert `LeaseStore.RenewIfDue` was called and the renewed lease has the same token and later expiry. Assert both fake sessions were disposed and that their connection-owner identifiers are distinct.

2. - [ ] **Run the focused test and confirm the hosted worker types are missing.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~JobWorkerTests"`

Expected: compilation fails with `CS0246` because `JobWorker` and `LeaseRenewer` are not defined.

3. - [ ] **Add hosting support, the renewing loop, and the complete normal coordinator.** Add `Microsoft.Extensions.Hosting.Abstractions` version `10.0.11` to Infrastructure. `LeaseRenewer` must call `delay.UntilAsync(current.RenewAfterUtc, token)`, then `LeaseStore.RenewIfDue`; a null renewal cancels its linked lease-loss token. `JobWorker` must inherit `BackgroundService`, create a fresh linked token per claim, and await the renewer and run task before accepting another job.

```csharp
// src/DataPitcher.Infrastructure/Worker/LeaseRenewer.cs
using DataPitcher.Infrastructure.Leasing;

namespace DataPitcher.Infrastructure.Worker;

public sealed class LeaseRenewer(LeaseStore leases, IWorkerDelay delay)
{
    public async Task RunAsync(LeaseGrant lease, TimeSpan ttl, CancellationTokenSource leaseLost, CancellationToken stopToken)
    {
        var current = lease;
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                await delay.UntilAsync(current.RenewAfterUtc, stopToken);
                var renewed = await leases.RenewIfDueAsync(current, ttl, stopToken);
                if (renewed is null) { leaseLost.Cancel(); return; }
                current = renewed;
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { }
    }
}
```

```csharp
// src/DataPitcher.Infrastructure/Worker/JobWorker.cs
using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Time;
using Microsoft.Extensions.Hosting;

namespace DataPitcher.Infrastructure.Worker;

public sealed class JobWorker(
    IJobControl jobs, IJobRunCatalog catalog, ITargetRunSessionFactory targets,
    ITransferReadSessionFactory sources, RecoveryCoordinator recovery, LeaseRenewer renewer,
    IControlCheckpointMirror mirror, IWorkerFaults faults, IWorkerDelay delay, IClock clock,
    string ownerId, TimeSpan leaseTtl, TimeSpan pollInterval) : BackgroundService
{
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        var claim = await jobs.TryClaimNextAsync(ownerId, leaseTtl, stoppingToken);
        if (claim is null) { await delay.UntilAsync(clock.UtcNow.Add(pollInterval), stoppingToken); continue; }
        await RunClaimAsync(claim, stoppingToken);
    }
}

private async Task RunClaimAsync(JobClaim claim, CancellationToken stoppingToken)
{
    using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    using var renewalStop = new CancellationTokenSource();
    var renewal = renewer.RunAsync(claim.Lease, leaseTtl, leaseLost, renewalStop.Token);
    try
    {
        await jobs.PrepareAsync(claim, leaseLost.Token);
        var run = await catalog.LoadAsync(claim.Job, leaseLost.Token);
        await using var target = await targets.OpenAsync(run, leaseLost.Token);
        var recovered = await recovery.RecoverAsync(claim, run, target, leaseLost.Token);
        await jobs.MarkRunningAsync(claim.Lease, leaseLost.Token);
        await using var source = await sources.OpenKeysetAsync(run, recovered.LastStableKey, leaseLost.Token);
        for (TransferUnit? unit; (unit = await source.ReadNextAsync(leaseLost.Token)) is not null;)
        {
            await faults.HitAsync(TransferFaultPoint.BeforeTargetCommit, leaseLost.Token);
            var checkpoint = await target.ApplyAsync(run, claim.Lease, unit, leaseLost.Token);
            await faults.HitAsync(TransferFaultPoint.AfterTargetCommitBeforeControlMirror, leaseLost.Token);
            await mirror.OverwriteAsync(checkpoint, leaseLost.Token);
            if (await jobs.GetStateAsync(claim.Job.JobId, leaseLost.Token) is JobState.Pausing)
            {
                await source.DiscardUncommittedAsync(leaseLost.Token);
                await jobs.MarkPausedAsync(claim.Lease, leaseLost.Token);
                return;
            }
        }
        await jobs.MarkVerifyingAsync(claim.Lease, leaseLost.Token);
    }
    finally { renewalStop.Cancel(); await renewal; }
}
}
```

Task 6 adds the command-aware cancellation and classified-failure catches around this complete success path. The worker invokes no HTTP object and never exposes a session outside `RunClaimAsync`.

4. - [ ] **Run the normal worker suite and confirm clock-driven ownership.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~JobWorkerTests"`

Expected: `Passed: 2. Failed: 0.` No test contains `Thread.Sleep` or `Task.Delay`; the gate makes renewal deterministic and confirms all sessions are worker-owned and disposed.

5. - [ ] **Commit hosted normal orchestration.**

Run: `git add src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj src/DataPitcher.Infrastructure/Worker/LeaseRenewer.cs src/DataPitcher.Infrastructure/Worker/JobWorker.cs tests/DataPitcher.UnitTests/Worker/WorkerTestSupport.cs tests/DataPitcher.UnitTests/Worker/JobWorkerTests.cs && git commit -m "feat: run claimed transfer jobs in background"`

### Task 4: Reconcile authoritative target checkpoints and repair surviving mutations

**Files:**
- Create: `src/DataPitcher.Infrastructure/Worker/RecoveryCoordinator.cs`, `tests/DataPitcher.UnitTests/Worker/RecoveryCoordinatorTests.cs`
- Modify: `tests/DataPitcher.UnitTests/Worker/WorkerTestSupport.cs`
- Test: `tests/DataPitcher.UnitTests/Worker/RecoveryCoordinatorTests.cs`

1. - [ ] **Write failing target-authority and repair tests.** Use a target fake with checkpoint batch 7/key 700 and a stale SQLite mirror at batch 4. Assert recovery opens with the newer SQLite lease token, calls the target fence/checkpoint operation before mirror overwrite, opens the source later with key 700, and never queries the mirror. Test mismatched target manifest hash throws `ManifestSealMismatchException`; a run with `SupportsDurableResume == false` throws `NonResumableInterruptedException`. Test repair of a disabled trigger changes its journal entry to `Repaired`; test an unrepaired untrusted constraint calls `QuarantineAsync` and keeps its state `Quarantined`.

2. - [ ] **Run the focused recovery suite and confirm the coordinator is absent.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~RecoveryCoordinatorTests"`

Expected: compilation fails with `CS0246` because `RecoveryCoordinator` does not exist.

3. - [ ] **Implement target-first recovery and journal repair.** `AcquireFenceReadCheckpointAndJournalAsync` is a provider contract with a strict implementation rule: in a target transaction, create the initial `(job_id, run_id)` checkpoint if absent; otherwise compare the stored seal ordinally, reject mismatch, advance its fence only when stored token is lower, read the checkpoint, and commit. It must not accept an equal or lower stale token. Its implementation writes the target mutation journal entry in the same target transaction as each durable mutation, records repair completion only after catalog verification, and preserves a quarantined record.

```csharp
// src/DataPitcher.Infrastructure/Worker/RecoveryCoordinator.cs
using DataPitcher.Infrastructure.Leasing;

namespace DataPitcher.Infrastructure.Worker;

public sealed class RecoveryCoordinator(IControlCheckpointMirror mirror)
{
    public async Task<TargetCheckpoint> RecoverAsync(JobClaim claim, TransferRun run, ITargetRunSession target, CancellationToken cancellationToken)
    {
        if (claim.IsInterrupted && !run.SupportsDurableResume)
            throw new NonResumableInterruptedException();

        var snapshot = await target.AcquireFenceReadCheckpointAndJournalAsync(run, claim.Lease, cancellationToken);
        if (!StringComparer.Ordinal.Equals(snapshot.Checkpoint.ManifestSealHash, run.ManifestSealHash))
            throw new ManifestSealMismatchException();

        var repaired = await target.RepairMutationsAsync(snapshot.Mutations, cancellationToken);
        foreach (var entry in repaired.Where(entry => entry.State is MutationJournalState.Quarantined))
            await target.QuarantineAsync(entry.Mutation, entry.Detail ?? "Target mutation repair could not be verified.", cancellationToken);

        await mirror.OverwriteAsync(snapshot.Checkpoint, cancellationToken);
        return snapshot.Checkpoint;
    }
}
```

Add the test fake’s target journal in the same task: it exposes only target checkpoint reads to the recovery coordinator, refuses a stale token, tracks fence acquisition before journal repair, and simulates the durable survivor catalogue state. Do not introduce a mirror read method anywhere in production.

4. - [ ] **Run the target-first recovery suite and confirm each decision is deterministic.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~RecoveryCoordinatorTests"`

Expected: `Passed: 4. Failed: 0.` The source resumes after stable key 700, a seal mismatch and non-resumable interruption never start a source session, and an unrepairable mutation remains quarantined.

5. - [ ] **Commit recovery and target journal coordination.**

Run: `git add src/DataPitcher.Infrastructure/Worker/RecoveryCoordinator.cs tests/DataPitcher.UnitTests/Worker/WorkerTestSupport.cs tests/DataPitcher.UnitTests/Worker/RecoveryCoordinatorTests.cs && git commit -m "feat: recover from target checkpoints and journals"`

### Task 5: Honor pause, resume, cancellation, and atomic SCC boundaries

**Files:**
- Create: none
- Modify: `src/DataPitcher.Infrastructure/Worker/JobWorker.cs`, `tests/DataPitcher.UnitTests/Worker/WorkerTestSupport.cs`, `tests/DataPitcher.UnitTests/Worker/JobWorkerTests.cs`
- Test: `tests/DataPitcher.UnitTests/Worker/JobWorkerTests.cs`

1. - [ ] **Write failing boundary tests.** Drive a source fake with committed batch 1 and a prefetched batch 2. Change the queue state to `Pausing` at the batch-1 commit barrier; assert batch 1 checkpoint/mirror writes occur, batch 2 is discarded rather than applied, source reader and connection are disposed, target staging has no durable batch-2 payload, and state is `Paused`. Reclaim the paused job after its resume command queues it; assert `OpenKeysetAsync` receives batch 1’s stable key rather than an offset. For cancellation, hold `ApplyAsync`, issue `Cancelling`, release the gate, and assert the same cancellation token reaches source, target, mirror, and SQLite fake methods before `Cancelled`. Finally, make one `AtomicComponent` unit pause while it is applying; assert the pause becomes `Paused` only after its single checkpoint and a simulated connection loss rolls the whole component back with no checkpoint advance.

2. - [ ] **Run the focused boundary tests and confirm the command-aware paths are incomplete.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~JobWorkerTests.Pause|FullyQualifiedName~JobWorkerTests.Resume|FullyQualifiedName~JobWorkerTests.Cancel|FullyQualifiedName~JobWorkerTests.Atomic"`

Expected: test failure reports that a prefetched unit was applied after `Pausing`, because the normal worker has not yet discarded it at the committed boundary.

3. - [ ] **Complete boundary handling without changing writer ownership.** After every successful `ApplyAsync`, check `Cancelling` before `Pausing`; cancellation calls both sessions’ `DiscardUncommittedAsync` with the linked token, then `MarkCancelledAsync`. Pause calls only the source discard method after the current successful commit, letting `await using` close its reader/connection. Require provider `ApplyAsync` to delete or mark reclaimable its own staging rows in the same fenced target transaction as business apply/checkpoint, so pause persists no payload. On resume, `JobStore.RequestResumeAsync` changes only `Paused -> Queued`; the next claim goes through target recovery and calls `OpenKeysetAsync(run, checkpoint.LastStableKey, token)`. Do not branch inside an atomic component: it is one `TransferUnit`, so the existing post-`ApplyAsync` boundary is the only pause observation point.

4. - [ ] **Run the focused boundary tests and confirm all session cleanup and resume semantics.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~JobWorkerTests.Pause|FullyQualifiedName~JobWorkerTests.Resume|FullyQualifiedName~JobWorkerTests.Cancel|FullyQualifiedName~JobWorkerTests.Atomic"`

Expected: `Passed: 4. Failed: 0.` Every asserted operation receives the linked cancellation token, resume has stable key 1 rather than an offset, and the SCC test has exactly one target commit or zero after interruption.

5. - [ ] **Commit command boundaries and atomic-component behavior.**

Run: `git add src/DataPitcher.Infrastructure/Worker/JobWorker.cs tests/DataPitcher.UnitTests/Worker/WorkerTestSupport.cs tests/DataPitcher.UnitTests/Worker/JobWorkerTests.cs && git commit -m "feat: pause resume and cancel worker runs"`

### Task 6: Prove fencing, commit-gap recovery, and fault classification without races

**Files:**
- Create: `tests/DataPitcher.UnitTests/Worker/WorkerFaultAndFenceTests.cs`
- Modify: `src/DataPitcher.Infrastructure/Worker/JobWorker.cs`, `tests/DataPitcher.UnitTests/Worker/WorkerTestSupport.cs`
- Test: `tests/DataPitcher.UnitTests/Worker/WorkerFaultAndFenceTests.cs`

1. - [ ] **Write deterministic fault tests using explicit `TaskCompletionSource` barriers.** Cover one fact per point: before target commit rolls back; during target write rolls back; after target commit before mirror leaves the target checkpoint advanced and mirror stale; simulated process interruption leaves no control transition after its last durable action; target connection loss has unknown disposition; cancellation reaches all sessions; command timeout is unknown; transient-then-success retries the uncommitted unit once; permanent failure records `Failed`; and recovery failure records `Failed`. For the critical zombie test, hold worker A immediately before target fence assertion, advance the manual clock past its lease, have worker B acquire and complete target fence acquisition, await that barrier, then release A. Assert A receives `TargetFenceLostException`, its target transaction records no applied business rows and no checkpoint advance, and B’s higher fence token remains current. Never use sleep, polling, or scheduler ordering as evidence.

2. - [ ] **Run the focused fault suite and confirm the unclassified paths fail.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~WorkerFaultAndFenceTests"`

Expected: failures identify the missing expected state handling—for example, the after-commit test observes a stale mirror without a later target-driven repair, and the zombie test observes an attempted stale apply rather than `TargetFenceLostException`.

3. - [ ] **Implement the exact fault policy.** `BeforeTargetCommit` and `DuringTargetWrite` throw `TransferAttemptException(NotCommitted, ...)`; only a classifier-confirmed transient instance is retried, via `IWorkerDelay`, with the same unit and current fence. `TargetConnectionLost` and `CommandTimeout` throw `TransferAttemptException(Unknown, ...)`; do not retry them. End the process-run path without a speculative SQLite write, then let the next claim use `RecoveryCoordinator` and the target checkpoint. `AfterTargetCommitBeforeControlMirror` and `ProcessInterrupted` likewise do not write a compensating mirror; recovery overwrites it from target. Map permanent, seal mismatch, non-resumable, unrepaired quarantine, and recovery failures to distinct persisted failure codes. The caught `OperationCanceledException` is `Cancelled` only when `IJobControl.GetStateAsync` reports `Cancelling`; host shutdown leaves the active persisted state for restart recovery.

4. - [ ] **Run the focused fault and fence suite and confirm each point is covered.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~WorkerFaultAndFenceTests"`

Expected: `Passed: 10. Failed: 0.` The fence test proves the stale commit fails after B’s target-fence barrier, and no test relies on elapsed wall-clock time.

5. - [ ] **Commit deterministic fault and fencing behavior.**

Run: `git add src/DataPitcher.Infrastructure/Worker/JobWorker.cs tests/DataPitcher.UnitTests/Worker/WorkerTestSupport.cs tests/DataPitcher.UnitTests/Worker/WorkerFaultAndFenceTests.cs && git commit -m "feat: fence worker commits and recover faults"`

### Task 7: Enforce the mirror boundary and complete full-slice evidence

**Files:**
- Create: none
- Modify: `tests/DataPitcher.ArchitectureTests/CheckpointMirrorBoundaryTests.cs`, `docs/test-coverage-matrix.md`
- Test: `tests/DataPitcher.ArchitectureTests/CheckpointMirrorBoundaryTests.cs`

1. - [ ] **Write the failing architecture and traceability assertions.** Extend the existing mirror-boundary test to allow only `CheckpointMirrorStore.OverwriteAsync` to mention `BatchCheckpointMirrors`, assert `CheckpointMirrorStore` declares no public getter, and inspect worker/recovery source for the absence of `BatchCheckpointMirrors` and `DerivedCheckpointMirror` reads. Add coverage-matrix rows for target-fenced worker ownership, target-checkpoint recovery, mutation-journal quarantine, boundary pause/resume, cancellation propagation, exact deterministic fault points, and idempotent starts.

2. - [ ] **Run the architecture test and confirm the old boundary is too narrow.**

Run: `dotnet test tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj --filter "FullyQualifiedName~CheckpointMirrorBoundaryTests"`

Expected: failure states that `CheckpointMirrorStore` is not yet included in the write-only allow-list or that a public mirror read is exposed.

3. - [ ] **Implement the one-way boundary and evidence rows.** The architecture test must inspect declared public instance methods and use `Assert.DoesNotContain(files, file => ...)` for forbidden production files. It must permit target checkpoint interfaces because they are target-provider contracts, not SQLite mirrors. Add the following exact matrix entries under `## Plans, transfer, verification, recovery, and security`:

```markdown
| DP-WORK-001 | Exactly one leased worker runs a job; a stale worker’s target apply is fenced by the target checkpoint token. | `architecture.md` §8; ADR 0001 §§2–4 | Slice 11 Tasks 2, 3, 6 | `JobStoreRecoveryTests`, `WorkerFaultAndFenceTests` | Planned |
| DP-REC-001 | Recovery reads the target checkpoint, rejects a seal mismatch, overwrites the SQLite mirror, and keyset-resumes after the committed stable key. | ADR 0001 §§3–4 | Slice 11 Task 4 | `RecoveryCoordinatorTests` | Planned |
| DP-REC-002 | Durable target mutation state is journalled, repaired and verified, or left quarantined. | `architecture.md` §§8–9 | Slice 11 Task 4 | `RecoveryCoordinatorTests` | Planned |
| DP-WORK-002 | Pause/cancel operate at committed units; atomic SCCs never pause mid-component. | `architecture.md` §8 | Slice 11 Task 5 | `JobWorkerTests` | Planned |
| DP-WORK-003 | Fault points, interruption, retry boundaries, and unknown commits are deterministic and never resolved from SQLite. | ADR 0001 §5 | Slice 11 Task 6 | `WorkerFaultAndFenceTests` | Planned |
```

4. - [ ] **Run the no-Docker and merged-coverage gates.**

Run: `scripts/test-unit.sh && scripts/test-all.sh`

Expected: the unit and architecture lanes pass without Docker; `test-all.sh` exits zero and prints `Merged coverage: line=100% branch=100% method=100.00%`. Do not lower, bypass, exclude, or weaken the gate.

5. - [ ] **Commit the enforced boundary and final evidence.**

Run: `git add tests/DataPitcher.ArchitectureTests/CheckpointMirrorBoundaryTests.cs docs/test-coverage-matrix.md && git commit -m "test: enforce worker recovery boundaries"`

## Self-Review

Covered: hosted background ownership outside HTTP; exclusive idempotent claim behavior; per-worker connection ownership; injectable-clock lease renewal; the target-fenced stale-worker barrier with no sleeps; every required deterministic fault point; transient-only retry at known uncommitted boundaries; unknown-commit recovery from the target; state persistence and failure codes; pause, keyset resume, cancellation, session disposal, and atomic-SCC boundaries; target checkpoint seal validation and mirror overwrite direction; non-resumable interrupted failure; durable SQL Server/PostgreSQL mutation survivors versus connection-scoped state; journal repair, verification, and quarantine; SQLite-only pure tests; warnings-as-errors; analyzer-safe xUnit assertions; and the sole merged 100 percent coverage gate.

Deferred: provider-native bulk writers, source-reader SQL, target checkpoint DDL and conditional SQL implementation, target staging DDL, physical bounded pipeline implementation, provider catalog repair queries, Docker-backed SQL Server/PostgreSQL proof, verification execution, API pause/resume/cancel endpoints, and DI composition. Those future implementations must satisfy the contracts here and add real-provider evidence; this slice neither implements nor substitutes them.

Consistency checked: types and method names are introduced before use—Task 1 defines `TransferRun`, `TargetCheckpoint`, `TransferUnit`, `IJobControl`, `ITargetRunSession`, `ITransferReadSession`, `IControlCheckpointMirror`, `IWorkerFaults`, and `IWorkerDelay`; Task 2 implements the mirror/control path; Task 3 introduces `LeaseRenewer`, `JobWorker`, and `WorkerTestSupport`; Task 4 introduces `RecoveryCoordinator`; later tasks use exactly `OpenKeysetAsync`, `AcquireFenceReadCheckpointAndJournalAsync`, `RepairMutationsAsync`, `ApplyAsync`, `OverwriteAsync`, `TryClaimNextAsync`, `PrepareAsync`, `MarkRunningAsync`, `MarkPausedAsync`, `MarkCancelledAsync`, and `MarkFailedAsync`. C# examples avoid keyword-named pattern variables, target-typed `new()` as a `params` argument, unmapped LINQ to DB columns, and xUnit analyzer-violating assertions.
