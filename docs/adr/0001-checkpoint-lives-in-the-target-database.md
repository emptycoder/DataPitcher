# ADR 0001: Checkpoint Lives in the Target Database

## Title

Checkpoint lives in the target database and is fenced with the target apply transaction.

## Status

Accepted.

## Date

2026-09-01.

## Context

DataPitcher is a .NET 10 application that transfers an exact, pre-sealed manifest of rows between relational databases, supporting SQL Server and PostgreSQL. Transfers run in an ASP.NET Core BackgroundService outside the HTTP request lifetime. Job orchestration state persists in a SQLite control database through LINQ to DB.

The production default transfer mode, ResumableStaged, bulk-writes each batch to a DataPitcher-owned target staging table, applies a set-based INSERT or UPSERT to the business table, commits, and records a checkpoint. Jobs must survive restart and resume at the last committed batch.

Writing the checkpoint to SQLite after the target transaction creates a durable commit gap. The target and SQLite control databases are separate durable stores with no distributed transaction. A crash between the target commit and SQLite checkpoint leaves committed target rows unknown to the control database.

Changing the order cannot make this safe. Target then checkpoint leaves an unknown replay outcome after a crash. Checkpoint then target can advance without applying the batch, causing silent data loss. The gap is inherent to two independently durable stores, not their ordering.

SQLite leasing has a related boundary. A lease is advisory scheduling state, while the target is mutated. A worker can lose its lease while descheduled and later wake to commit. SQLite alone cannot prevent that zombie from changing the target.

## Decision

The authoritative resume checkpoint is stored in a DataPitcher-owned target checkpoint table. For every batch, the target apply and checkpoint update occur in one target transaction. This collapses commit and checkpoint into one atomic durable act and removes SQLite from the correctness-critical path.

The target checkpoint is keyed by `(job_id, run_id)` and records the last committed batch sequence, last committed stable key, cumulative row count, sealed manifest hash, and fence token. The checkpoint update is part of the transaction that applies the staged batch to the business table.

SQLite retains only a derived mirror for UI and scheduling. It is repaired from the target on every recovery and never consulted for correctness. An interrupted batch committed if and only if the checkpoint advanced, so there is no uncertain state to reconcile.

Recovery: acquire the job lease; run mutation-journal repair; read the target checkpoint; refuse resumption if its manifest seal hash differs from the current manifest; overwrite the SQLite mirror; resume strictly after the recorded stable key.

SQLite stores one lease row per job: `owner_id`, `expires_utc`, and monotonic `fence_token`, used for scheduling only. On acquisition, the new owner increments the token in SQLite and the target checkpoint before writing. Each apply conditionally updates the target checkpoint only if its stored token equals the worker's. Zero affected rows abort the transaction.

The target database adjudicates ownership at commit time. A worker that loses its lease cannot later commit because it missed expiry. Staging rows include `(job_id, fence_token, batch_seq)` and apply filters on the current token; zombie rows are inert and reclaimable.

## Consequences

This eliminates the commit gap, gives deterministic recovery, and makes multi-instance execution safe without distributed transactions. A zombie cannot commit after its token is superseded. SQLite remains useful for scheduling and UI without determining correctness.

DataPitcher must be permitted to create and write its own checkpoint table in the target database. A target that permits writes only to business tables cannot support ResumableStaged. This is a capability requirement that must be surfaced and must block plan sealing when it is absent.

The SQLite mirror may be stale. Reading it for transfer correctness is a defect. Code review must reject this dependency, and an architecture test should enforce the boundary.

Implementation pressure may favor moving the checkpoint back to SQLite for convenience. That would restore the exact commit gap this decision removes, while remaining invisible until a production crash.

## Alternatives Considered

Writing the checkpoint to SQLite after the target commit was rejected because a crash leaves an unknown replay outcome. Writing the SQLite checkpoint before the target commit was rejected because a crash can skip an unapplied batch and silently lose data. Neither ordering removes the independent-store gap.

MSDTC distributed transactions were rejected. PostgreSQL `PREPARE TRANSACTION` and two-phase commit were also rejected. Orphaned prepared transactions hold locks and can block vacuum indefinitely, creating a worse failure mode than the gap being addressed.

A SQLite lease without a target fence was rejected because it cannot prevent a stale worker from committing changes to the target database.

## Verification

Kill the process between the target apply commit and any SQLite write, then verify recovery reads the target checkpoint, repairs the SQLite mirror, and resumes correctly. This guards against restoring SQLite to the commit protocol.

Test that a mismatched target checkpoint manifest seal hash causes recovery to refuse resumption. Test that recovery resumes strictly after the target checkpoint's last committed stable key.

Test fencing with two workers: acquire a newer lease and token after an earlier worker stages work, then attempt the earlier apply. Verify its conditional checkpoint update affects zero rows, its transaction aborts, and stale staging rows are not applied. Add an architecture test or equivalent review rule rejecting SQLite checkpoint reads in correctness decisions.

## Open Questions

- How and when is the initial target checkpoint row created before the first lease acquisition increments its fence token?
