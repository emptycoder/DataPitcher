
# Execution State

As of 2026-09-02.

This file exists for resumability. A future session should be able to read this
and know exactly where to pick up — no other context required.

## Status

**Nine slices are COMPLETE:**

1. The domain spine — provider-independent schema model, dependency graph, SCC
   condensation, and demand-driven closure algorithm.
2. The PostgreSQL closure store — a real-database implementation of
   `IClosureStore` against PostgreSQL.
3. The control database with job state machine and fence-token leases.
4. The provider-neutral authorization model.
5. The selection query AST and SQL generation.
6. Transfer plan construction, canonical hashing and sealing.
7. Row-cycle detection and cycle strategy selection.
8. The SQL Server closure store — the same `IClosureStore` contract implemented
   against SQL Server.
9. The bounded transfer pipeline.

**What does NOT yet exist:** transfer execution against a real database,
verification, the job worker and recovery, ASP.NET authentication wiring, the
Minimal API, and the entire frontend. None of these have any code in this
repository yet. Everything shipped so far is planning, modeling, and read-path
infrastructure — no data has ever actually moved from a source database to a
target database through this codebase.

## Verified quality gates

- `scripts/test-all.sh` exits 0 with **645 tests**: 557 unit, 5 architecture,
  43 PostgreSQL integration, 40 SQL Server integration.
- Merged coverage is **100% line, 100% branch, 100% method**.
- `dotnet build`: clean, **zero warnings**, under warnings-as-errors.
- The full gate takes about **310 seconds**, dominated by the SQL Server lane
  running under binary translation on this arm64 host.
- The gate is enforced only in `scripts/test-all.sh`, which merges each
  project's coverage report with ReportGenerator rather than summing per-project
  numbers — a project at less than 100% cannot hide behind another project's
  surplus.
- There is exactly **one** documented coverage exclusion in the whole project:
  a source-generated regex matcher, excluded as generated code. It is the only
  exclusion anywhere in the project; the 100 percent figure above is not
  padded by additional undisclosed exclusions.

## The central validation

All **31 closure behavioural tests** pass, with unchanged assertions, against
**both real PostgreSQL and real SQL Server** — the same test bodies exercising
two different `IClosureStore` implementations. Probe batching is proven at the
wire on both providers: 40 keys requested in one generation produce **exactly
2 probe commands**, not 40, confirming batching is real rather than a paper
claim. This matters because a naive closure store would issue one probe per
key, and at scale that difference is the gap between a usable tool and one
that saturates the source database with round trips.

## Environment

- Docker Engine 29.3.1, arm64 Apple Silicon.
- The `docker` CLI is **not** on `PATH`; its binary lives inside the Docker
  Desktop application bundle. This affects shell-out tooling but not
  Testcontainers.
- PostgreSQL runs as a **native arm64** image, ready in about **1 second**.
- SQL Server has **no native arm64 image** and runs under binary translation,
  ready in about **8 seconds**, using about **1.08 GiB** at readiness. Two
  containers together use **2.28 GiB** of a **7.65 GiB** allocation.
- Concurrent Docker load from several agents running container tests at once
  has previously produced pre-login handshake failures. These looked like
  product defects on first read but were reproduced as resource contention,
  not a code bug — worth remembering before chasing a similar symptom again.
  If the two upcoming worktrees both run the Docker-backed integration lanes
  at the same time, expect the same class of flaky handshake symptom and
  check contention before suspecting the newly written code.

## Notable findings so far

Independent review and coverage-driven testing across the nine slices found
real production defects, not stylistic nits, including:

- A text-match operator that generated the wrong SQL pattern.
- Binary stable keys that could not be constructed as originally implemented.
- Key ordering that depended on machine locale rather than being deterministic.
- A relationship type that carried no join columns at all — unimplementable
  against any real database, caught before it reached a provider slice.

(See prior slice review notes for the complete list per slice; this is not
exhaustive.)

## Key decisions that must not be silently reversed

1. A single demand-driven closure is used, not the specification's original
   two-phase candidate-then-final design (ADR 0004).
2. Target-satisfied pruning requires the target foreign key to be both
   enforced and trusted; otherwise the row must be transferred rather than
   pruned (ADR 0004).
3. The authoritative resume checkpoint lives in the target database inside the
   apply transaction; the control database mirror must never be consulted for
   correctness (ADR 0001).
4. LINQ to DB is excluded from the transfer write path because its bulk copy
   silently degrades under some conditions and does not report which strategy
   it actually used (ADR 0005).
5. StrictExact verification is blocked when triggers exist on a planned target
   table, and is unavailable at all for DirectFast (ADR 0002).
6. Stable keys do **not** normalize CLR types; the type of each key component
   is taken as-is from the schema.
7. Virtual stable keys remain deferred.
8. A malformed schema degrades to a `Blocked` result rather than throwing an
   exception.
9. Topological layer index 0 is the parents, ordered in transfer order.
10. Graph and closure output is canonically ordered using ordinal comparison,
    so results are deterministic across runs.
11. Overlapping closure roots with differing conflict policies are rejected
    rather than silently resolved.

## Exact next executable action

Execute the two plans now ready, in parallel isolated worktrees so that
concurrent builds on each stream cannot corrupt the other's build output:

- `docs/plans/2026-09-02-slice-10-postgresql-transfer-execution.md`
- `docs/plans/2026-09-02-slice-11-job-worker-and-recovery.md`
