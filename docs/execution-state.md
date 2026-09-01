# Execution State

As of 2026-09-02.

This file exists for resumability. A future session should be able to read this
and know exactly where to pick up — no other context required.

## Status

The architecture and specification phase is **COMPLETE**. Implementation has
**NOT** started. No production code exists anywhere in this repository. The
repository currently contains documentation only.

## Environment

- Repository: `/Users/yaroslavsanko/Repositories/DataPitcher`, git initialised, branch `main`.
- Work branch: `architecture/foundation`, checked out in worktree
  `.worktrees/architecture-foundation` (git-ignored, not part of `main`'s tree).
- .NET SDK 10.0.400 — available.
- Node v25.2.1 — available.
- Docker — **NOT INSTALLED**. The `docker` command is not found on this machine.

## Completed

Thirteen documents have been produced:

- `docs/architecture.md` — system map, solution structure, dependency rules.
- `docs/dependency-semantics.md` — the normative closure and satisfaction rules.
- `docs/spec-deviations.md` — the 17 deliberate deviations from the original specification.
- `docs/threat-model.md` — 30 design-phase threats.
- `docs/roadmap.md` — 11 slices with Docker status and exit gates per slice.
- `docs/plans/2026-09-02-slice-1-domain-spine.md` — the task-by-task TDD plan for Slice 1.
- `docs/adr/0001` — the resume checkpoint lives in the target database, with fencing.
- `docs/adr/0002` — exact-set verification scope and limits.
- `docs/adr/0003` — referential cycle strategy per provider.
- `docs/adr/0004` — a single demand-driven closure, superseding the two-phase design.
- `docs/adr/0005` — native bulk writers and the limits of LINQ to DB.
- `docs/adr/0006` — authentication and authorization architecture.
- `docs/adr/0007` — frontend architecture and coverage isolation.

## Quality gate status

Stated honestly: no build exists, no tests exist, no coverage has been
measured, no lint is configured. Every quality gate is **NOT YET APPLICABLE**
— none are passing, none are failing, because there is nothing yet to run
them against. No claim of passing anything has been made anywhere in this
repository.

## Blockers

One genuine external blocker: **Docker is not installed** on this machine, so
Testcontainers integration tests, Docker Compose end-to-end runs, and
Playwright browser tests cannot execute here. Under the original
specification's own definition, an inaccessible Docker daemon counts as a
genuine external blocker, not a shortcut taken by choice. Slice 1 was
deliberately scoped to require none of it, so this blocker does not stop the
next executable action below.

## Key decisions a future session must not silently reverse

1. A single demand-driven closure is used, not the specification's original
   two-phase candidate-then-final design (ADR 0004).
2. Target-satisfied pruning requires the target foreign key to be both
   enforced and trusted; otherwise the row must be transferred rather than
   pruned (ADR 0004).
3. The authoritative resume checkpoint lives in the target database inside
   the apply transaction; the control database mirror must never be
   consulted for correctness (ADR 0001).
4. LINQ to DB is excluded from the transfer write path because its bulk
   copy silently degrades under some conditions and does not report which
   strategy it actually used (ADR 0005).
5. StrictExact verification is blocked when triggers exist on a planned
   target table, and is unavailable at all for DirectFast (ADR 0002).

## Exact next executable action

Open `docs/plans/2026-09-02-slice-1-domain-spine.md` and execute **Task 1,
Step 1**: create `DataPitcher.sln`, the `DataPitcher.Core` project, the
`DataPitcher.UnitTests` project, `Directory.Build.props`, and `global.json`.
Then verify that `dotnet build` and `dotnet test` both succeed with zero
tests collected. Slice 1 requires no Docker and can proceed immediately —
this is not blocked by the Docker gap above.

## Not verified

Stated plainly, as an honesty record: these documents were produced by
delegated agents, and their internal prose has not been independently
re-read line by line by the coordinating session. A separate cross-document
consistency review was run, and its findings are recorded in the commit
history rather than restated here. Do not treat the existence of these
documents as evidence of correctness beyond what that review actually
checked — confidence should not be overstated in either direction.
