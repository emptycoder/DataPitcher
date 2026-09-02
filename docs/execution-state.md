
# Execution State

As of 2026-09-02.

This file exists for resumability. A future session should be able to read this
and know exactly where to pick up — no other context required.

## Status

**Slice 1 (Domain Spine) is COMPLETE.** Slices 2 through 11 are **NOT started**.
The provider-independent domain model exists and is fully tested. No database
code, no API code, and no frontend code exists anywhere in this repository yet.

## Environment

- Repository: `/Users/yaroslavsanko/Repositories/DataPitcher`, git initialised, branch `main`.
- Work branch: `architecture/foundation`, checked out in worktree
  `.worktrees/architecture-foundation` (git-ignored, not part of `main`'s tree).
- .NET SDK 10.0.400 — available.
- Node — available.
- Docker Engine 29.3.1 is running: arm64 Apple Silicon, 10 CPUs, 7.65 GiB RAM
  available to the VM.
- `docker` CLI is not on `PATH` (binary is in the Docker Desktop bundle), which
  affects shell-out tooling but not Testcontainers.
- Testcontainers .NET smoke-test path is verified: `postgres:17-alpine` (native
  arm64) and `mcr.microsoft.com/mssql/server:2022-latest` (amd64 image, running
  under emulation) both become ready quickly and executed real queries.

## What Slice 1 delivered

Eight tasks, executed test-first per `docs/plans/2026-09-02-slice-1-domain-spine.md`:

1. **Solution scaffold** — `DataPitcher.sln`, `DataPitcher.Core`, `DataPitcher.UnitTests`, `Directory.Build.props`, `global.json`, building and testing green with zero tests collected.
2. **`StableKey` value type** — composite key with binary and ordinal-ordering semantics, giving deterministic, locale-independent comparison and hashing across composite column values.
3. **Schema model with stable-key selection** — table, column, foreign key and unique-constraint definitions, plus the logic that selects which constraint on a table serves as its stable key.
4. **Dependency graph** — child-to-parent edges built from foreign keys, with bidirectional adjacency for traversal in both directions.
5. **Tarjan strongly connected components** — with condensation of cycles into single nodes and a transfer-ordered topological layering of the condensed graph.
6. **Closure store abstraction** — the `IClosureStore` interface plus an in-memory fake implementation, giving a seam for the algorithm to be tested without any real database.
7. **Demand-driven, target-aware closure algorithm** — computes the exact-set closure of rows to transfer, pruning subtrees already satisfied in the target when the relevant foreign key is enforced and trusted.
8. **Architecture tests, coverage gate scripts, and a CI workflow** — enforcing dependency-direction rules and a 100% coverage bar on every future change.

## Verified quality gates

- `dotnet test DataPitcher.sln`: **118 unit tests + 3 architecture tests = 121 tests, 0 failing.**
- `dotnet build`: clean, **zero warnings**, under warnings-as-errors.
- `./scripts/test-all.sh`: **100% line coverage, 100% branch coverage, 103 of 103 methods covered**, gate script exits 0.
- This gate is known to work, not merely assumed to: it was observed **FAILING at 96.42%** coverage before the final equality-contract tests were added, and passed only after those tests closed the gap. That failure-then-pass sequence is the evidence the gate actually enforces something.
- Updated suite is **122 tests** including a Testcontainers smoke test; coverage is still
  **100% line, 100% branch, and 100% method**; `scripts/test-all.sh` now
  requires Docker.

## Evidence from independent review

Every one of the eight tasks was reviewed by an agent that did not write it, and every review found real defects. The most significant findings:

- Binary stable keys were unconstructible as originally implemented.
- String-based key ordering depended on machine locale rather than being deterministic.
- A nullable unique constraint was initially accepted as a valid row identity.
- A zero-column unique constraint would have collapsed every row in a table to a single shared identity.
- The dependency graph did not canonicalise edge endpoints, risking duplicate/divergent edge representations.
- Topological layers were inverted relative to transfer order.
- Graph output was not order-invariant, risking nondeterministic transfer ordering across runs.
- Closure root ordering could silently suppress the default safety blocker under certain input orderings.
- The closure relationship type lacked value equality — undetected, this would have destroyed exact-set minimality the first time a real provider slice relied on set deduplication.

Separately, a differential test ran **25,000 randomly generated schemas** through this implementation and an independently written reference implementation of the same closure semantics, and found **zero extra rows and zero missing rows** across all of them.

## Blockers

**Resolved blocker:** container-based testing was previously blocked by external Docker availability and is now lifted and verified. Testcontainers now boots PostgreSQL (`postgres:17-alpine`) and SQL Server (`mcr.microsoft.com/mssql/server:2022-latest`) containers and executes real queries in one second-level startup path. Caveats remain: SQL Server is amd64-only and runs under emulation, and memory headroom is tight for the full two-postgres/two-SQL Server container design because each SQL Server container needs at least 2 GB.

## Key decisions that must not be silently reversed

1. A single demand-driven closure is used, not the specification's original two-phase candidate-then-final design (ADR 0004).
2. Target-satisfied pruning requires the target foreign key to be both enforced and trusted; otherwise the row must be transferred rather than pruned (ADR 0004).
3. The authoritative resume checkpoint lives in the target database inside the apply transaction; the control database mirror must never be consulted for correctness (ADR 0001).
4. LINQ to DB is excluded from the transfer write path because its bulk copy silently degrades under some conditions and does not report which strategy it actually used (ADR 0005).
5. StrictExact verification is blocked when triggers exist on a planned target table, and is unavailable at all for DirectFast (ADR 0002).
6. Stable keys do **not** normalize CLR types; the type of each key component is taken as-is from the schema.
7. Virtual stable keys are deferred — not implemented in Slice 1.
8. A malformed schema degrades to a `Blocked` result rather than throwing an exception.
9. Topological layer index 0 is the parents, ordered in transfer order.
10. Graph and closure output is canonically ordered using ordinal comparison, so results are deterministic across runs.
11. Overlapping closure roots with differing conflict policies are rejected rather than silently resolved.

## Exact next executable action

Begin **Slice 2: the control database and job state machine**, as required by
`docs/roadmap.md` (the next unblocked executable slice). Then execute
`docs/plans/2026-09-02-slice-2-postgresql-closure-store.md` to implement a
PostgreSQL-backed closure store so Slice 1's thirty-one closure behavioural tests can
be re-run unchanged against a real database.
